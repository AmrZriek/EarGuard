using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using EarGuard.Config;

namespace EarGuard.Audio
{
    public class VolumeClampedEventArgs : EventArgs
    {
        public GuardedDevice Device { get; private set; }
        public float OldVolume { get; private set; }
        public float ClampedVolume { get; private set; }

        public VolumeClampedEventArgs(GuardedDevice device, float oldVolume, float clampedVolume)
        {
            Device = device;
            OldVolume = oldVolume;
            ClampedVolume = clampedVolume;
        }
    }

    /// <summary>
    /// Describes what a volume adjustment attempt actually did.
    /// </summary>
    public enum VolumeAdjustmentOutcome
    {
        /// <summary>The endpoint was already at or below the target, so nothing was changed.</summary>
        AlreadySafe,
        /// <summary>The endpoint was louder than the target and was lowered.</summary>
        Lowered,
        /// <summary>The endpoint is not guarded, so it was deliberately left untouched.</summary>
        Skipped,
        /// <summary>The endpoint was invalidated and recovery was requested.</summary>
        RecoveryRequested,
        /// <summary>The hardware state or write result is unknown; retry without invalidating.</summary>
        Failed
    }

    public struct VolumeAdjustmentResult
    {
        // Explicit readonly fields rather than auto-properties: the C# 5 compiler shipped with
        // .NET Framework cannot assign an auto-property backing field inside a struct constructor
        // (CS0843), and the documented no-build-tools path compiles this file with that compiler.
        private readonly VolumeAdjustmentOutcome _outcome;
        private readonly float _oldVolume;
        private readonly float _newVolume;

        public VolumeAdjustmentOutcome Outcome { get { return _outcome; } }
        public float OldVolume { get { return _oldVolume; } }
        public float NewVolume { get { return _newVolume; } }

        public VolumeAdjustmentResult(VolumeAdjustmentOutcome outcome, float oldVolume, float newVolume)
        {
            _outcome = outcome;
            _oldVolume = oldVolume;
            _newVolume = newVolume;
        }
    }

    public class AudioEngine : IDisposable
    {
        public static readonly Guid ContextGuid = Guid.NewGuid();
        public const int WatchdogIntervalMs = 100;

        /// <summary>
        /// How long after a plug-in EarGuard keeps holding the endpoint at the safe plug-in limit.
        ///
        /// Windows restores a reconnected endpoint's previous volume shortly after it appears, so a
        /// single application at detection time gets overwritten. This window lets the plug-in limit
        /// survive that restore. It is deliberately a cap, never a target: if the endpoint is quieter
        /// than the limit, EarGuard still does nothing.
        /// </summary>
        public const int SafePlugInGraceMs = 3000;

        /// <summary>
        /// How often discovery retries run while waiting for a freshly reported endpoint to appear.
        ///
        /// Retries exist because Windows finishes re-indexing a USB endpoint slightly after it
        /// raises the notification. The interval is deliberately coarser than the watchdog, which
        /// already re-caps attached endpoints every <see cref="WatchdogIntervalMs"/>. Retries also
        /// stop as soon as a scan finds the endpoint set unchanged, so one device change costs a
        /// couple of scans instead of one per tick.
        /// </summary>
        public const int DiscoveryRetryIntervalMs = 500;

        // Tolerance used when deciding whether a write is even necessary.
        private const float VolumeEpsilon = 0.0001f;

        private readonly ConfigStore _configStore;
        private readonly EarGuardConfig _config;
        private readonly object _syncRoot = new object();
        private readonly List<GuardedDevice> _guardedDevices = new List<GuardedDevice>();
        private readonly HashSet<string> _seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IMMDeviceEnumerator _enumerator;
        private AudioNotificationClient _notificationClient;
        private Timer _watchdogTimer;
        private Timer _discoveryTimer;
        private DateTime _discoveryUntilUtc;
        private bool _structureChangedSincePublish;
        private int _discoveryObservedChange;
        private Thread _workerThread;
        private BlockingCollection<Action> _workQueue;
        private volatile bool _workerRunning;
        private IntPtr _mmcssHandle = IntPtr.Zero;
        private int _resyncPending;
        private bool _isDisposed;
        private string _defaultDeviceId = string.Empty;

        [ThreadStatic]
        private static bool t_isMmcssRegistered;

        public bool IsMmcssActive { get; private set; }
        public event EventHandler<VolumeClampedEventArgs> VolumeClamped;
        public event EventHandler DevicesChanged;
        public event EventHandler<GuardedDevice> VolumeChanged;

        public EarGuardConfig Config
        {
            get { return _config; }
        }

        public IReadOnlyList<GuardedDevice> GuardedDevices
        {
            get
            {
                lock (_syncRoot)
                {
                    return _guardedDevices.ToArray();
                }
            }
        }

        /// <summary>
        /// Device id of the system's current default playback endpoint, or an empty string when it
        /// cannot be determined. Used by the UI so the window opens on the device the user actually
        /// hears rather than whichever endpoint happens to enumerate first.
        /// </summary>
        public string DefaultDeviceId
        {
            get
            {
                lock (_syncRoot)
                {
                    return _defaultDeviceId;
                }
            }
        }

        public AudioEngine(ConfigStore configStore, EarGuardConfig config)
        {
            if (configStore == null) throw new ArgumentNullException("configStore");
            if (config == null) throw new ArgumentNullException("config");
            _configStore = configStore;
            _config = config;
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_enumerator != null) return;

                // Start prioritized Pro Audio MMCSS worker thread
                _workQueue = new BlockingCollection<Action>();
                _workerRunning = true;
                _workerThread = new Thread(WorkerThreadProc)
                {
                    Name = "EarGuard Pro-Audio Worker",
                    IsBackground = true,
                    Priority = ThreadPriority.Highest
                };
                _workerThread.Start();

                try
                {
                    _enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                    _notificationClient = new AudioNotificationClient(OnHardwareDevicesChanged);
                    _notificationClient.DeviceDisconnected += OnDeviceDisconnected;
                    _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("AudioEngine start failed to init COM enumerator: " + ex.Message);
                }

                ScanDevices();

                // Defense-in-depth ultra-fast watchdog running every 100ms
                _watchdogTimer = new Timer(WatchdogTick, null, WatchdogIntervalMs, WatchdogIntervalMs);
            }
        }

        private void WorkerThreadProc()
        {
            int taskIndex = 0;
            try
            {
                _mmcssHandle = CoreAudioConstants.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
                if (_mmcssHandle != IntPtr.Zero)
                {
                    IsMmcssActive = true;
                }
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("MMCSS registration failed: " + ex.Message);
            }

            while (_workerRunning)
            {
                try
                {
                    Action action;
                    if (_workQueue.TryTake(out action, 500))
                    {
                        if (action != null && !_isDisposed)
                        {
                            action();
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Worker thread error: " + ex.Message);
                }
            }

            if (_mmcssHandle != IntPtr.Zero)
            {
                try
                {
                    CoreAudioConstants.AvRevertMmThreadCharacteristics(_mmcssHandle);
                }
                catch { }
                _mmcssHandle = IntPtr.Zero;
                IsMmcssActive = false;
            }
        }

        private void PostWork(Action action)
        {
            if (!_workerRunning || _isDisposed || _workQueue == null) return;
            try
            {
                _workQueue.Add(action);
            }
            catch { }
        }

        /// <summary>
        /// Releases a COM object without letting a release failure escape.
        ///
        /// ReleaseComObject throws when the reference is not a COM object, and it is called while
        /// iterating discovered endpoints. An exception there would abort the rest of the scan and
        /// skip device removal, leaving endpoints guarded after they have gone away, so release
        /// failures are swallowed to keep scan progress intact.
        /// </summary>
        private static void SafeReleaseComObject(object comObject)
        {
            if (comObject == null) return;
            try { Marshal.ReleaseComObject(comObject); } catch (ArgumentException) { }
        }

        private static void EnsureThreadMmcss()
        {
            if (t_isMmcssRegistered) return;
            try
            {
                int taskIndex = 0;
                CoreAudioConstants.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                t_isMmcssRegistered = true;
            }
            catch { }
        }

        public void ScanDevices()
        {
            lock (_syncRoot)
            {
                if (_enumerator == null || _isDisposed) return;

                // Set when the guarded set itself changes: an endpoint attaches, or one goes away.
                // Volume-only re-capping happens on every scan but must not be published, otherwise
                // each scan rewrites settings.json and rebuilds the UI device list.
                bool structureChanged = false;
                bool defaultChanged = false;

                IMMDeviceCollection col = null;
                try
                {
                    int hr = _enumerator.EnumAudioEndpoints(
                        CoreAudioConstants.E_RENDER,
                        CoreAudioConstants.DEVICE_STATE_ACTIVE,
                        out col
                    );

                    if (hr != 0 || col == null)
                    {
                        ReinitializeEnumerator();
                        if (_enumerator != null)
                        {
                            _enumerator.EnumAudioEndpoints(
                                CoreAudioConstants.E_RENDER,
                                CoreAudioConstants.DEVICE_STATE_ACTIVE,
                                out col
                            );
                        }
                    }

                    if (col == null) return;

                    string defaultIdBefore = _defaultDeviceId;
                    RefreshDefaultDeviceId();
                    defaultChanged = !string.Equals(defaultIdBefore, _defaultDeviceId, StringComparison.OrdinalIgnoreCase);

                    uint count = 0;
                    col.GetCount(out count);

                    var currentActiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice dev = null;
                        col.Item(i, out dev);
                        if (dev == null) continue;

                        string devId = null;
                        dev.GetId(out devId);
                        if (string.IsNullOrEmpty(devId))
                        {
                            SafeReleaseComObject(dev);
                            continue;
                        }

                        currentActiveIds.Add(devId);

                        // Check if already hooked
                        var existing = _guardedDevices.Find(d => string.Equals(d.DeviceId, devId, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            SafeReleaseComObject(dev);
                            if (_seenDeviceIds.Add(devId))
                            {
                                existing.SafePlugInUntilUtc = DateTime.UtcNow.AddMilliseconds(SafePlugInGraceMs);
                            }

                            // Inside the plug-in grace window, hold the endpoint at the safe plug-in
                            // limit so Windows' restore of the previous volume cannot undo it. Outside
                            // the window, re-assert the ceiling. Both are caps and can only lower volume.
                            float existingTarget = IsWithinSafePlugInWindow(existing)
                                ? Math.Min(existing.Config.SafePlugInVol, existing.Config.MaxLimit)
                                : existing.Config.MaxLimit;

                            var existingOutcome = ApplyVolumeCeiling(existing, existingTarget);
                            if (existingOutcome.Outcome == VolumeAdjustmentOutcome.Lowered)
                            {
                                RaiseVolumeClamped(existing, existingOutcome.OldVolume, existingOutcome.NewVolume);
                            }
                            continue;
                        }

                        // Read complete friendly names
                        string fullFriendlyName = GetDeviceProperty(dev, CoreAudioConstants.PKEY_Device_FriendlyName);
                        string interfaceName = GetDeviceProperty(dev, CoreAudioConstants.PKEY_Interface_FriendlyName);
                        string adapterDesc = GetDeviceProperty(dev, CoreAudioConstants.PKEY_DeviceDesc);

                        string chosenName;
                        if (!string.IsNullOrEmpty(fullFriendlyName))
                        {
                            chosenName = fullFriendlyName;
                        }
                        else if (!string.IsNullOrEmpty(interfaceName) && !string.IsNullOrEmpty(adapterDesc))
                        {
                            chosenName = string.Format("{0} ({1})", interfaceName, adapterDesc);
                        }
                        else if (!string.IsNullOrEmpty(interfaceName))
                        {
                            chosenName = interfaceName;
                        }
                        else if (!string.IsNullOrEmpty(adapterDesc))
                        {
                            chosenName = adapterDesc;
                        }
                        else
                        {
                            chosenName = "Audio Endpoint";
                        }
                        // Retrieve or create per-device config
                        var devConfig = _configStore.GetOrCreateDeviceConfig(_config, devId, chosenName);

                        // Activate volume endpoint
                        Guid iid = CoreAudioConstants.IID_IAudioEndpointVolume;
                        object volObj = null;
                        int actHr = dev.Activate(ref iid, CoreAudioConstants.CLSCTX_ALL, IntPtr.Zero, out volObj);
                        var vol = volObj as IAudioEndpointVolume;

                        if (actHr != 0 || vol == null)
                        {
                            SafeReleaseComObject(dev);
                            continue;
                        }

                        var guarded = new GuardedDevice
                        {
                            DeviceId = devId,
                            DeviceName = chosenName,
                            Description = !string.IsNullOrEmpty(adapterDesc) ? adapterDesc : interfaceName,
                            DeviceCom = dev,
                            VolumeControl = vol,
                            Config = devConfig
                        };

                        // Read the current hardware level so the UI shows a real value before any
                        // adjustment is attempted.
                        float curVol = 0f;
                        int ghr = vol.GetMasterVolumeLevelScalar(out curVol);
                        if (ghr == 0 && !float.IsNaN(curVol) && !float.IsInfinity(curVol))
                        {
                            guarded.CurrentVolume = curVol;
                        }

                        // Freshly connected endpoints are capped at the safe plug-in level, and stay
                        // capped for a short grace window so Windows' restore of the previous volume
                        // cannot silently undo it. Endpoints we already knew about are re-capped at
                        // their ceiling. Both are caps, not targets: if the hardware comes up quieter
                        // than the cap, it is left alone.
                        bool isNewDevice = !_seenDeviceIds.Contains(devId);
                        _seenDeviceIds.Add(devId);

                        if (isNewDevice)
                        {
                            guarded.SafePlugInUntilUtc = DateTime.UtcNow.AddMilliseconds(SafePlugInGraceMs);
                        }

                        float scanTarget = isNewDevice
                            ? Math.Min(devConfig.SafePlugInVol, devConfig.MaxLimit)
                            : devConfig.MaxLimit;

                        var scanOutcome = ApplyVolumeCeiling(guarded, scanTarget);
                        if (scanOutcome.Outcome == VolumeAdjustmentOutcome.RecoveryRequested)
                        {
                            guarded.Dispose();
                            continue;
                        }
                        if (scanOutcome.Outcome == VolumeAdjustmentOutcome.Lowered)
                        {
                            RaiseVolumeClamped(guarded, scanOutcome.OldVolume, scanOutcome.NewVolume);
                        }

                        // Register COM volume callback
                        var callback = new AudioEndpointCallback(data => HandleVolumeNotification(guarded, data));
                        guarded.CallbackInstance = callback;
                        vol.RegisterControlChangeNotify(callback);

                        _guardedDevices.Add(guarded);
                        structureChanged = true;
                    }

                    // Remove any devices that were disconnected.
                    // The seen-device set must be pruned in lockstep, otherwise it grows without
                    // bound and, worse, a device that is re-attached later is still considered
                    // "already known" and would miss its safe plug-in treatment.
                    for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                    {
                        var g = _guardedDevices[i];
                        if (!currentActiveIds.Contains(g.DeviceId))
                        {
                            _seenDeviceIds.Remove(g.DeviceId);
                            g.Dispose();
                            _guardedDevices.RemoveAt(i);
                            structureChanged = true;
                        }
                    }
                }
                finally
                {
                    SafeReleaseComObject(col);
                }

                _structureChangedSincePublish |= structureChanged;
                if (structureChanged)
                    Interlocked.Exchange(ref _discoveryObservedChange, 1);

                // Publish only when the device set or the default endpoint actually changed. A scan
                // that merely re-asserted existing caps has nothing new to tell anyone.
                if (!_structureChangedSincePublish && !defaultChanged) return;

                _structureChangedSincePublish = false;
                _configStore.Save(_config);
                RaiseDevicesChanged();
            }
        }

        private void OnHardwareDevicesChanged()
        {
            // One work item keeps the ordering explicit: open the retry window, then scan. If that
            // scan finds the endpoint Windows was re-indexing, the next tick stops retrying.
            // Attached endpoints are already protected by the watchdog throughout the grace window,
            // so nothing here may sleep on the sole recovery worker.
            PostWork(() =>
            {
                if (_isDisposed) return;

                Interlocked.Exchange(ref _discoveryObservedChange, 0);

                lock (_syncRoot)
                {
                    if (_isDisposed) return;
                    _discoveryUntilUtc = DateTime.UtcNow.AddMilliseconds(SafePlugInGraceMs);
                    if (_discoveryTimer == null)
                        _discoveryTimer = new Timer(DiscoveryRetryTick, null, DiscoveryRetryIntervalMs, DiscoveryRetryIntervalMs);
                    else
                        _discoveryTimer.Change(DiscoveryRetryIntervalMs, DiscoveryRetryIntervalMs);
                }

                ScanDevices();
            });
        }

        private void DiscoveryRetryTick(object state)
        {
            // Stop once a scan has already reported a change: the endpoint has appeared, and the
            // watchdog covers it from here. Otherwise keep retrying until the window closes.
            if (Interlocked.CompareExchange(ref _discoveryObservedChange, 1, 1) == 1)
            {
                StopDiscoveryRetries();
                return;
            }

            lock (_syncRoot)
            {
                if (_isDisposed) return;
                if (DateTime.UtcNow >= _discoveryUntilUtc)
                {
                    StopDiscoveryRetries();
                    return;
                }
            }

            ScanDevices();
        }

        private void StopDiscoveryRetries()
        {
            lock (_syncRoot)
            {
                if (_discoveryTimer != null)
                    _discoveryTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void OnDeviceDisconnected(string deviceId)
        {
            // Retain the transition even if removal/reconnection both precede enumeration.
            // Do not take the engine lock on the COM notification thread.
            PostWork(() =>
            {
                lock (_syncRoot)
                {
                    _seenDeviceIds.Remove(deviceId);
                    var existing = _guardedDevices.Find(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                        existing.SafePlugInUntilUtc = DateTime.UtcNow.AddMilliseconds(SafePlugInGraceMs);
                }
            });
        }

        private void HandleVolumeNotification(GuardedDevice guarded, AUDIO_VOLUME_NOTIFICATION_DATA data)
        {
            // Core Audio may deliver a callback while a setter/unregister call is waiting.
            // Never block that COM thread on our writer lock; defer contention to the worker.
            // Own echoes need no work: the successful writer publishes the new level itself.
            if (data.guidEventContext == ContextGuid) return;
            if (!Monitor.TryEnter(_syncRoot))
            {
                PostWork(() => HandleVolumeNotification(guarded, data));
                return;
            }
            try
            {
            if (_isDisposed || guarded == null || guarded.Config == null) return;

            // Prioritize incoming COM callback thread under Pro-Audio MMCSS
            EnsureThreadMmcss();

            float newVol = data.fMasterVolume;

            if (float.IsNaN(newVol) || float.IsInfinity(newVol)) return;

            // While a freshly plugged endpoint is inside its grace window, the effective limit is the
            // safe plug-in limit, so a late Windows volume restore is pulled straight back down
            // instead of waiting for the next watchdog tick.
            float effectiveLimit = IsWithinSafePlugInWindow(guarded)
                ? Math.Min(guarded.Config.SafePlugInVol, guarded.Config.MaxLimit)
                : guarded.Config.MaxLimit;

            bool shouldClamp = ShouldClampVolume(
                guarded.Config.Enabled,
                newVol,
                effectiveLimit,
                data.guidEventContext,
                ContextGuid
            );
            if (shouldClamp)
            {
                // Immediate downward-only clamp through the single guarded write path.
                var outcome = ApplyVolumeCeiling(guarded, effectiveLimit);
                if (outcome.Outcome == VolumeAdjustmentOutcome.Lowered)
                {
                    RaiseVolumeClamped(guarded, outcome.OldVolume, outcome.NewVolume);
                }
            }
            else
            {
                guarded.CurrentVolume = newVol;
            }

            RaiseVolumeChanged(guarded);
            }
            finally
            {
                Monitor.Exit(_syncRoot);
            }
        }

        private static bool IsWithinSafePlugInWindow(GuardedDevice device)
        {
            return device != null && DateTime.UtcNow < device.SafePlugInUntilUtc;
        }

        private void WatchdogTick(object state)
        {
            if (_isDisposed) return;

            lock (_syncRoot)
            {
                for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                {
                    var guarded = _guardedDevices[i];
                    if (guarded.VolumeControl == null || guarded.Config == null || !guarded.Config.Enabled)
                        continue;

                    // Hold freshly plugged endpoints at the plug-in limit for the grace window so a
                    // late Windows volume restore cannot lift them back up to their old level.
                    float target = IsWithinSafePlugInWindow(guarded)
                        ? Math.Min(guarded.Config.SafePlugInVol, guarded.Config.MaxLimit)
                        : guarded.Config.MaxLimit;

                    var outcome = ApplyVolumeCeiling(guarded, target);
                    if (outcome.Outcome == VolumeAdjustmentOutcome.Lowered)
                    {
                        RaiseVolumeClamped(guarded, outcome.OldVolume, outcome.NewVolume);
                    }
                }

            }
        }

        public void HandleSystemResume()
        {
            lock (_syncRoot)
            {
                if (_isDisposed) return;

                // Instant high-priority clamp on all currently active endpoints
                for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                {
                    var guarded = _guardedDevices[i];
                    if (guarded.VolumeControl == null || guarded.Config == null || !guarded.Config.Enabled)
                        continue;

                    guarded.SafePlugInUntilUtc = DateTime.UtcNow.AddMilliseconds(SafePlugInGraceMs);
                    float target = Math.Min(guarded.Config.SafePlugInVol, guarded.Config.MaxLimit);
                    var outcome = ApplyVolumeCeiling(guarded, target);
                    if (outcome.Outcome == VolumeAdjustmentOutcome.Lowered)
                    {
                        RaiseVolumeClamped(guarded, outcome.OldVolume, outcome.NewVolume);
                    }
                }

                // Clear seen devices so that ScanDevices treats re-enumerating endpoints as freshly plugged in
                _seenDeviceIds.Clear();

                // Run instant scan to detect and clamp re-initialized audio endpoints immediately
                ScanDevices();
            }
        }

        private void TriggerFastResync(string deviceIdToInvalidate = null)
        {
            lock (_syncRoot)
            {
                if (_isDisposed) return;

                if (!string.IsNullOrEmpty(deviceIdToInvalidate))
                {
                    for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(_guardedDevices[i].DeviceId, deviceIdToInvalidate, StringComparison.OrdinalIgnoreCase))
                        {
                            _guardedDevices[i].Dispose();
                            _guardedDevices.RemoveAt(i);
                            break;
                        }
                    }
                    _seenDeviceIds.Remove(deviceIdToInvalidate);
                }
                else
                {
                    for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                    {
                        var g = _guardedDevices[i];
                        if (g.VolumeControl == null) continue;
                        float tmp;
                        int hr = g.VolumeControl.GetMasterVolumeLevelScalar(out tmp);
                        if (hr != 0 && IsRpcErrorCode(hr))
                        {
                            _seenDeviceIds.Remove(g.DeviceId);
                            g.Dispose();
                            _guardedDevices.RemoveAt(i);
                        }
                    }
                }
            }

            if (Interlocked.CompareExchange(ref _resyncPending, 1, 0) == 0)
            {
                PostWork(() =>
                {
                    try
                    {
                        ScanDevices();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _resyncPending, 0);
                    }
                });
            }
        }

        /// <summary>
        /// Resolves the current default playback endpoint. This is advisory only; failure simply
        /// means the UI falls back to selecting the first available device.
        /// </summary>
        private void RefreshDefaultDeviceId()
        {
            _defaultDeviceId = string.Empty;
            if (_enumerator == null) return;

            IMMDevice defaultDevice = null;
            try
            {
                int hr = _enumerator.GetDefaultAudioEndpoint(
                    CoreAudioConstants.E_RENDER,
                    CoreAudioConstants.E_CONSOLE,
                    out defaultDevice
                );

                if (hr != 0 || defaultDevice == null) return;

                string id = null;
                int idHr = defaultDevice.GetId(out id);
                if (idHr == 0 && !string.IsNullOrEmpty(id))
                {
                    _defaultDeviceId = id;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Could not resolve default audio endpoint: " + ex.Message);
            }
            finally
            {
                if (defaultDevice != null)
                {
                    try { Marshal.ReleaseComObject(defaultDevice); } catch { }
                }
            }
        }

        private void ReinitializeEnumerator()
        {
            try
            {
                if (_enumerator != null && _notificationClient != null)
                {
                    try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
                }
                if (_enumerator != null)
                {
                    try { Marshal.ReleaseComObject(_enumerator); } catch { }
                    _enumerator = null;
                }
                _enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                _notificationClient = new AudioNotificationClient(OnHardwareDevicesChanged);
                _notificationClient.DeviceDisconnected += OnDeviceDisconnected;
                _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to reinitialize enumerator: " + ex.Message);
            }
        }

        public static bool IsRpcErrorCode(int hr)
        {
            return hr == CoreAudioConstants.RPC_S_SERVER_UNAVAILABLE
                || hr == CoreAudioConstants.RPC_E_DISCONNECTED
                || (uint)hr == 0x88890004  // AUDCLNT_E_DEVICE_INVALIDATED
                || (uint)hr == 0x80070490; // ERROR_NOT_FOUND
        }

        public static bool IsRpcOrComException(Exception ex)
        {
            if (ex == null) return false;
            var comEx = ex as COMException;
            if (comEx != null)
            {
                return IsRpcErrorCode(comEx.ErrorCode);
            }
            return ex is InvalidComObjectException;
        }

        /// <summary>
        /// Lowers a guarded endpoint to <paramref name="targetScalar"/> if and only if the hardware
        /// is currently louder than that value.
        ///
        /// This is EarGuard's only endpoint writer. The engine lock serializes the entire
        /// read/compare/write transaction, including UI, watchdog, resume and callback callers.
        /// Windows exposes separate read and set operations: an external writer can still change
        /// volume between them, so this ordering guarantee applies only to EarGuard writers.
        /// </summary>
        public VolumeAdjustmentResult ApplyVolumeCeiling(GuardedDevice guarded, float targetScalar)
        {
            lock (_syncRoot)
            {
            if (_isDisposed || guarded == null || guarded.VolumeControl == null || guarded.Config == null)
            {
                return new VolumeAdjustmentResult(VolumeAdjustmentOutcome.Skipped, 0f, 0f);
            }

            if (!guarded.Config.Enabled)
            {
                return new VolumeAdjustmentResult(VolumeAdjustmentOutcome.Skipped, guarded.CurrentVolume, guarded.CurrentVolume);
            }

            // Normalise the requested target into the legal scalar range before doing anything else.
            if (float.IsNaN(targetScalar) || targetScalar < 0f) targetScalar = 0f;
            if (targetScalar > 1f) targetScalar = 1f;

            float current;
            int readHr;
            try
            {
                readHr = guarded.VolumeControl.GetMasterVolumeLevelScalar(out current);
            }
            catch (Exception ex)
            {
                bool invalidated = IsRpcOrComException(ex);
                if (invalidated) TriggerFastResync(guarded.DeviceId);
                return new VolumeAdjustmentResult(invalidated ? VolumeAdjustmentOutcome.RecoveryRequested : VolumeAdjustmentOutcome.Failed, guarded.CurrentVolume, guarded.CurrentVolume);
            }

            if (readHr != 0)
            {
                bool invalidated = IsRpcErrorCode(readHr);
                if (invalidated) TriggerFastResync(guarded.DeviceId);
                return new VolumeAdjustmentResult(invalidated ? VolumeAdjustmentOutcome.RecoveryRequested : VolumeAdjustmentOutcome.Failed, guarded.CurrentVolume, guarded.CurrentVolume);
            }

            // A non-finite scalar cannot be reasoned about; refuse to write anything.
            if (float.IsNaN(current) || float.IsInfinity(current))
            {
                return new VolumeAdjustmentResult(VolumeAdjustmentOutcome.Failed, guarded.CurrentVolume, guarded.CurrentVolume);
            }

            // Nothing to do: the endpoint is already quiet enough. Critically, this branch is why
            // EarGuard never raises volume, even when the user raises the ceiling above the current
            // level or re-enables protection on a quiet device.
            if (current <= targetScalar + VolumeEpsilon)
            {
                guarded.CurrentVolume = current;
                return new VolumeAdjustmentResult(VolumeAdjustmentOutcome.AlreadySafe, current, current);
            }

            // Defence in depth: the value we are about to write is explicitly capped at the level we
            // just read from hardware, so even a nonsensical target can never become an increase.
            float safeTarget = Math.Max(0f, Math.Min(targetScalar, current));

            int writeHr;
            try
            {
                Guid ctx = ContextGuid;
                writeHr = guarded.VolumeControl.SetMasterVolumeLevelScalar(safeTarget, ref ctx);
            }
            catch (Exception ex)
            {
                bool invalidated = IsRpcOrComException(ex);
                if (invalidated) TriggerFastResync(guarded.DeviceId);
                return new VolumeAdjustmentResult(invalidated ? VolumeAdjustmentOutcome.RecoveryRequested : VolumeAdjustmentOutcome.Failed, current, current);
            }

            if (writeHr != 0)
            {
                bool invalidated = IsRpcErrorCode(writeHr);
                if (invalidated) TriggerFastResync(guarded.DeviceId);
                else System.Diagnostics.Debug.WriteLine("Volume clamp write failed: 0x" + writeHr.ToString("X8"));
                return new VolumeAdjustmentResult(invalidated ? VolumeAdjustmentOutcome.RecoveryRequested : VolumeAdjustmentOutcome.Failed, current, current);
            }

            guarded.CurrentVolume = safeTarget;
            return new VolumeAdjustmentResult(VolumeAdjustmentOutcome.Lowered, current, safeTarget);
            }
        }


        public static bool ShouldClampVolume(
            bool isDeviceEnabled,
            float currentVolumeScalar,
            float maxLimitScalar,
            Guid eventContext,
            Guid engineContext)
        {
            if (!isDeviceEnabled) return false;
            if (eventContext == engineContext) return false; // Own echo
            return currentVolumeScalar > (maxLimitScalar + 0.0001f);
        }

        public static int ScalarToPercent(float scalar)
        {
            if (scalar <= 0.0f) return 0;
            if (scalar >= 1.0f) return 100;
            return (int)Math.Round(scalar * 100f);
        }

        public static float PercentToScalar(int percent)
        {
            if (percent <= 0) return 0.0f;
            if (percent >= 100) return 1.0f;
            return (float)percent / 100f;
        }

        private static string GetDeviceProperty(IMMDevice dev, PROPERTYKEY key)
        {
            if (dev == null) return string.Empty;
            try
            {
                IPropertyStore store = null;
                int hr = dev.OpenPropertyStore(CoreAudioConstants.STGM_READ, out store);
                if (hr == 0 && store != null)
                {
                    PROPVARIANT pv = new PROPVARIANT();
                    try
                    {
                        int ghr = store.GetValue(ref key, out pv);
                        if (ghr == 0 && pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                        {
                            return Marshal.PtrToStringUni(pv.pwszVal);
                        }
                    }
                    finally
                    {
                        CoreAudioConstants.PropVariantClear(ref pv);
                        Marshal.ReleaseComObject(store);
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private void RaiseVolumeClamped(GuardedDevice dev, float oldVol, float clampedVol)
        {
            var handler = VolumeClamped;
            if (handler != null)
            {
                handler(this, new VolumeClampedEventArgs(dev, oldVol, clampedVol));
            }
        }

        private void RaiseDevicesChanged()
        {
            var handler = DevicesChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseVolumeChanged(GuardedDevice dev)
        {
            var handler = VolumeChanged;
            if (handler != null)
            {
                handler(this, dev);
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_isDisposed) return;
                _isDisposed = true;

                if (_watchdogTimer != null)
                {
                    _watchdogTimer.Dispose();
                    _watchdogTimer = null;
                }
                if (_discoveryTimer != null)
                {
                    _discoveryTimer.Dispose();
                    _discoveryTimer = null;
                }

                _workerRunning = false;
                if (_workQueue != null)
                {
                    try { _workQueue.CompleteAdding(); } catch { }
                }
                if (_workerThread != null && _workerThread.IsAlive)
                {
                    _workerThread.Join(500);
                    _workerThread = null;
                }
                if (_workQueue != null)
                {
                    try { _workQueue.Dispose(); } catch { }
                    _workQueue = null;
                }
                if (_enumerator != null && _notificationClient != null)
                {
                    try
                    {
                        _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                    }
                    catch { }
                    _notificationClient = null;
                }

                foreach (var dev in _guardedDevices)
                {
                    dev.Dispose();
                }
                _guardedDevices.Clear();

                if (_enumerator != null)
                {
                    try { Marshal.ReleaseComObject(_enumerator); } catch { }
                    _enumerator = null;
                }
            }
        }
    }
}
