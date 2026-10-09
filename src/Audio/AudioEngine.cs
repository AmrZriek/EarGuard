using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        /// already re-caps attached endpoints every <see cref="WatchdogIntervalMs"/>. The full
        /// bounded arrival window is retained for staggered endpoints; failures retry at this rate.
        /// </summary>
        public const int DiscoveryRetryIntervalMs = 500;

        // Tolerance used when deciding whether a write is even necessary.
        private const float VolumeEpsilon = 0.0001f;

        private readonly ConfigStore _configStore;
        private readonly EarGuardConfig _config;
        private readonly object _syncRoot;
        private readonly Func<IMMDeviceEnumerator> _createEnumerator;
        private readonly ConcurrentQueue<Action> _publications = new ConcurrentQueue<Action>();
        private int _publishingEvents;
        private bool _notificationsRegistered;
        private bool _discoveryFailed;
        private int _hardwareChangePending;
        private readonly List<GuardedDevice> _guardedDevices = new List<GuardedDevice>();
        private readonly HashSet<string> _seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IMMDeviceEnumerator _enumerator;
        private AudioNotificationClient _notificationClient;
        private Timer _watchdogTimer;
        private Timer _discoveryTimer;
        private long _discoveryUntil;
        private bool _structureChangedSincePublish;
        private Thread _workerThread;
        private BlockingCollection<Action> _workQueue;
        private volatile bool _workerRunning;
        private IntPtr _mmcssHandle = IntPtr.Zero;
        private int _resyncPending;
        private bool _isDisposed;
        private string _defaultDeviceId = string.Empty;


        public bool IsMmcssActive { get; private set; }
        public event EventHandler<VolumeClampedEventArgs> VolumeClamped;
        public event EventHandler DevicesChanged;
        internal event Action<bool> DiscoveryCompleted;
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
            : this(configStore, config, () => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject()) { }

        internal AudioEngine(ConfigStore configStore, EarGuardConfig config, Func<IMMDeviceEnumerator> createEnumerator)
        {
            if (configStore == null) throw new ArgumentNullException("configStore");
            if (config == null) throw new ArgumentNullException("config");
            _configStore = configStore;
            _syncRoot = configStore.SyncRoot;
            _createEnumerator = createEnumerator;
            _config = config;
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_workerRunning || _isDisposed) return;

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

                // Defense-in-depth ultra-fast watchdog running every 100ms
                _watchdogTimer = new Timer(WatchdogTick, null, WatchdogIntervalMs, WatchdogIntervalMs);
                _discoveryTimer = new Timer(DiscoveryRetryTick, null, DiscoveryRetryIntervalMs, DiscoveryRetryIntervalMs);
                RequestScan();
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
            if (_isDisposed) Cleanup();
        }

        private void PostWork(Action action)
        {
            var queue = _workQueue;
            if (!_workerRunning || _isDisposed || queue == null) return;
            try
            {
                queue.Add(action);
            }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Releases native objects while allowing deterministic tests to supply managed stand-ins.
        /// </summary>
        private static void SafeReleaseComObject(object comObject)
        {
            if (comObject == null) return;
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }

        internal static long Deadline(int milliseconds)
        {
            return Stopwatch.GetTimestamp() + (long)(milliseconds * (double)Stopwatch.Frequency / 1000);
        }

        public void ScanDevices()
        {
            bool publish = false;
            bool scanSucceeded = false;
            try
            {
                lock (_syncRoot)
                {
                    if (_isDisposed) return;
                    string defaultBefore = _defaultDeviceId;
                    _discoveryFailed = false;
                    IMMDeviceCollection collection = null;
                    try
                    {
                        if (_enumerator == null) ReinitializeEnumerator();
                        if (_enumerator == null) { _discoveryFailed = true; return; }
                        RegisterNotifications();
                        int hr = _enumerator.EnumAudioEndpoints(CoreAudioConstants.E_RENDER,
                            CoreAudioConstants.DEVICE_STATE_ACTIVE, out collection);
                        if (hr != 0 || collection == null)
                        {
                            _discoveryFailed = true;
                            ReinitializeEnumerator();
                            return;
                        }
                        uint count;
                        if (collection.GetCount(out count) != 0) { _discoveryFailed = true; return; }
                        RefreshDefaultDeviceId();
                        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        bool complete = true;
                        for (uint i = 0; i < count; i++)
                        {
                            IMMDevice device = null;
                            try
                            {
                                if (collection.Item(i, out device) != 0 || device == null)
                                { complete = false; continue; }
                                string id;
                                if (device.GetId(out id) != 0 || string.IsNullOrEmpty(id))
                                { complete = false; continue; }
                                active.Add(id);
                                var existing = _guardedDevices.Find(d => string.Equals(d.DeviceId, id, StringComparison.OrdinalIgnoreCase));
                                if (existing != null)
                                {
                                    if (_seenDeviceIds.Add(id)) existing.SafePlugInUntil = Deadline(SafePlugInGraceMs);
                                    RegisterVolumeCallback(existing);
                                    float target = IsWithinSafePlugInWindow(existing)
                                        ? Math.Min(existing.Config.SafePlugInVol, existing.Config.MaxLimit) : existing.Config.MaxLimit;
                                    var result = ApplyVolumeCeiling(existing, target);
                                    if (result.Outcome == VolumeAdjustmentOutcome.Lowered)
                                        RaiseVolumeClamped(existing, result.OldVolume, result.NewVolume);
                                }
                                else if (AttachEndpoint(device, id)) device = null; // ownership transferred
                                else _discoveryFailed = true;
                            }
                            catch (COMException ex) { complete = false; Debug.WriteLine(ex.Message); }
                            catch (InvalidComObjectException ex) { complete = false; Debug.WriteLine(ex.Message); }
                            finally { SafeReleaseComObject(device); }
                        }
                        // Incomplete enumeration is not evidence of removal.
                        if (complete)
                        {
                            for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                            {
                                var device = _guardedDevices[i];
                                if (active.Contains(device.DeviceId)) continue;
                                _seenDeviceIds.Remove(device.DeviceId);
                                device.Dispose();
                                _guardedDevices.RemoveAt(i);
                                _structureChangedSincePublish = true;
                            }
                        }
                        else _discoveryFailed = true;
                    }
                    catch (COMException ex) { _discoveryFailed = true; Debug.WriteLine(ex.Message); ReinitializeEnumerator(); }
                    catch (InvalidComObjectException ex) { _discoveryFailed = true; Debug.WriteLine(ex.Message); ReinitializeEnumerator(); }
                    finally
                    {
                        SafeReleaseComObject(collection);
                        scanSucceeded = !_discoveryFailed;
                        publish = _structureChangedSincePublish || defaultBefore != _defaultDeviceId;
                        _structureChangedSincePublish = false;
                        // Even a failed scan must publish a preceding invalidation/removal.
                        if (publish) RaiseDevicesChanged();
                    }
                }
            }
            finally
            {
                PublishEvents();
                if (publish) _configStore.Save(_config);
                var completed = DiscoveryCompleted;
                if (completed != null) completed(scanSucceeded);
            }
        }

        private bool AttachEndpoint(IMMDevice device, string id)
        {
            string name = GetDeviceProperty(device, CoreAudioConstants.PKEY_Device_FriendlyName);
            string interfaceName = GetDeviceProperty(device, CoreAudioConstants.PKEY_Interface_FriendlyName);
            string description = GetDeviceProperty(device, CoreAudioConstants.PKEY_DeviceDesc);
            if (string.IsNullOrEmpty(name)) name = !string.IsNullOrEmpty(interfaceName)
                ? (string.IsNullOrEmpty(description) ? interfaceName : interfaceName + " (" + description + ")")
                : (string.IsNullOrEmpty(description) ? "Audio Endpoint" : description);
            object volumeObject = null;
            GuardedDevice guarded = null;
            bool attached = false;
            try
            {
                Guid iid = CoreAudioConstants.IID_IAudioEndpointVolume;
                int hr = device.Activate(ref iid, CoreAudioConstants.CLSCTX_ALL, IntPtr.Zero, out volumeObject);
                var volume = volumeObject as IAudioEndpointVolume;
                if (hr != 0 || volume == null) return false;
                guarded = new GuardedDevice
                {
                    DeviceId = id, DeviceName = name, Description = string.IsNullOrEmpty(description) ? interfaceName : description,
                    QueueNotification = _publications.Enqueue,
                    VolumeControl = volume, Config = _configStore.GetOrCreateDeviceConfig(_config, id, name)
                };
                volumeObject = null;
                if (!guarded.Config.Enabled)
                {
                    float current;
                    if (volume.GetMasterVolumeLevelScalar(out current) != 0) return false;
                    if (!float.IsNaN(current) && !float.IsInfinity(current)) guarded.CurrentVolume = current;
                }
                if (_seenDeviceIds.Add(id)) guarded.SafePlugInUntil = Deadline(SafePlugInGraceMs);
                float target = IsWithinSafePlugInWindow(guarded)
                    ? Math.Min(guarded.Config.SafePlugInVol, guarded.Config.MaxLimit) : guarded.Config.MaxLimit;
                var result = ApplyVolumeCeiling(guarded, target);
                if (result.Outcome == VolumeAdjustmentOutcome.RecoveryRequested) return false;
                RegisterVolumeCallback(guarded);
                guarded.DeviceCom = device;
                _guardedDevices.Add(guarded);
                attached = true;
                _structureChangedSincePublish = true;
                if (result.Outcome == VolumeAdjustmentOutcome.Lowered)
                    RaiseVolumeClamped(guarded, result.OldVolume, result.NewVolume);
                return true;
            }
            finally
            {
                SafeReleaseComObject(volumeObject);
                if (!attached && guarded != null) guarded.Dispose();
                if (!attached) _seenDeviceIds.Remove(id);
            }
        }

        private void RegisterVolumeCallback(GuardedDevice device)
        {
            if (device.CallbackRegistered) return;
            if (device.CallbackInstance == null)
                device.CallbackInstance = new AudioEndpointCallback(data => HandleVolumeNotification(device, data));
            try { device.CallbackRegistered = device.VolumeControl.RegisterControlChangeNotify(device.CallbackInstance) == 0; }
            catch (COMException ex) { Debug.WriteLine(ex.Message); }
            catch (InvalidComObjectException ex) { Debug.WriteLine(ex.Message); }
            // Keep the device attached: the watchdog still protects it while registration retries.
            if (!device.CallbackRegistered) _discoveryFailed = true;
        }

        public void RequestScan()
        {
            if (Interlocked.CompareExchange(ref _resyncPending, 1, 0) != 0) return;
            if (!_workerRunning || _isDisposed) { Interlocked.Exchange(ref _resyncPending, 0); return; }
            PostWork(() =>
            {
                try
                {
                    if (Interlocked.Exchange(ref _hardwareChangePending, 0) != 0)
                        lock (_syncRoot) _discoveryUntil = Deadline(SafePlugInGraceMs);
                    ScanDevices();
                }
                finally
                {
                    Interlocked.Exchange(ref _resyncPending, 0);
                    if (Interlocked.CompareExchange(ref _hardwareChangePending, 0, 0) != 0) RequestScan();
                }
            });
        }

        private void OnHardwareDevicesChanged()
        {
            Interlocked.Exchange(ref _hardwareChangePending, 1);
            RequestScan();
        }

        private void DiscoveryRetryTick(object state)
        {
            lock (_syncRoot)
            {
                if (_isDisposed) return;
                if (_enumerator != null && _notificationsRegistered && !_discoveryFailed
                    && Stopwatch.GetTimestamp() >= _discoveryUntil) return;
            }
            RequestScan();
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
                        existing.SafePlugInUntil = Deadline(SafePlugInGraceMs);
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
            guarded.QueueNotification = _publications.Enqueue;

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
                PublishEvents();
            }
        }

        private static bool IsWithinSafePlugInWindow(GuardedDevice device)
        {
            return device != null && Stopwatch.GetTimestamp() < device.SafePlugInUntil;
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
            PublishEvents();
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

                    guarded.SafePlugInUntil = Deadline(SafePlugInGraceMs);
                    float target = Math.Min(guarded.Config.SafePlugInVol, guarded.Config.MaxLimit);
                    var outcome = ApplyVolumeCeiling(guarded, target);
                    if (outcome.Outcome == VolumeAdjustmentOutcome.Lowered)
                    {
                        RaiseVolumeClamped(guarded, outcome.OldVolume, outcome.NewVolume);
                    }
                }

                // Clear seen devices so that ScanDevices treats re-enumerating endpoints as freshly plugged in
                _seenDeviceIds.Clear();

                _discoveryUntil = Deadline(SafePlugInGraceMs);
                RequestScan();
            }
            PublishEvents();
        }

        private void TriggerFastResync(string deviceIdToInvalidate = null)
        {
            lock (_syncRoot)
            {
                if (_isDisposed) return;
                // A queued scan may already own the request slot; retain timer recovery.
                _discoveryFailed = true;

                if (!string.IsNullOrEmpty(deviceIdToInvalidate))
                {
                    for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(_guardedDevices[i].DeviceId, deviceIdToInvalidate, StringComparison.OrdinalIgnoreCase))
                        {
                            _guardedDevices[i].Dispose();
                            _guardedDevices.RemoveAt(i);
                            _structureChangedSincePublish = true;
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
                            _structureChangedSincePublish = true;
                        }
                    }
                }
            }

            RequestScan();
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

        private void RegisterNotifications()
        {
            if (_notificationsRegistered || _enumerator == null) return;
            if (_notificationClient == null)
            {
                _notificationClient = new AudioNotificationClient(OnHardwareDevicesChanged);
                _notificationClient.DeviceDisconnected += OnDeviceDisconnected;
            }
            _notificationsRegistered = _enumerator.RegisterEndpointNotificationCallback(_notificationClient) == 0;
        }

        private void ReinitializeEnumerator()
        {
            if (_enumerator != null)
            {
                try
                {
                    if (_notificationsRegistered) _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
                catch (COMException ex) { Debug.WriteLine(ex.Message); }
                catch (InvalidComObjectException ex) { Debug.WriteLine(ex.Message); }
                finally { SafeReleaseComObject(_enumerator); _enumerator = null; }
            }
            _notificationsRegistered = false;
            try { _enumerator = _createEnumerator(); }
            catch (COMException ex) { Debug.WriteLine("Enumerator unavailable: " + ex.Message); }
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
            try { return ApplyVolumeCeilingCore(guarded, targetScalar); }
            finally { PublishEvents(); }
        }

        private VolumeAdjustmentResult ApplyVolumeCeilingCore(GuardedDevice guarded, float targetScalar)
        {
            lock (_syncRoot)
            {
            if (_isDisposed || guarded == null || guarded.VolumeControl == null || guarded.Config == null)
            {
                return new VolumeAdjustmentResult(VolumeAdjustmentOutcome.Skipped, 0f, 0f);
            }

            guarded.QueueNotification = _publications.Enqueue;
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
                float previous = guarded.CurrentVolume;
                guarded.CurrentVolume = current;
                if (guarded.CurrentVolume != previous) RaiseVolumeChanged(guarded);
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
            IPropertyStore store = null;
            try
            {
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

                    }
                }
            }
            catch (COMException ex) { Debug.WriteLine(ex.Message); }
            finally { SafeReleaseComObject(store); }
            return string.Empty;
        }

        private void RaiseVolumeClamped(GuardedDevice dev, float oldVol, float clampedVol)
        {
            var handler = VolumeClamped;
            if (handler != null)
            {
                _publications.Enqueue(() => handler(this, new VolumeClampedEventArgs(dev, oldVol, clampedVol)));
            }
        }

        private void RaiseDevicesChanged()
        {
            var handler = DevicesChanged;
            if (handler != null)
            {
                _publications.Enqueue(() => handler(this, EventArgs.Empty));
            }
        }

        private void RaiseVolumeChanged(GuardedDevice dev)
        {
            var handler = VolumeChanged;
            if (handler != null)
            {
                _publications.Enqueue(() => handler(this, dev));
            }
        }

        private void PublishEvents()
        {
            if (Monitor.IsEntered(_syncRoot)) return;
            do
            {
                // One consumer preserves event order; competing writers never wait on subscribers.
                if (Interlocked.CompareExchange(ref _publishingEvents, 1, 0) != 0) return;
                try
                {
                    Action publication;
                    while (_publications.TryDequeue(out publication)) publication();
                }
                finally { Interlocked.Exchange(ref _publishingEvents, 0); }
                // A producer may have queued work while this consumer was finishing its drain.
            } while (!_publications.IsEmpty);
        }

        public void Dispose()
        {
            Thread worker;
            lock (_syncRoot)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                worker = _workerThread;

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
            }
            // The worker can need the engine lock while exiting. Never join under that lock.
            if (worker == Thread.CurrentThread) return; // worker finally owns cleanup below
            if (worker != null) worker.Join();
            Cleanup();
        }

        private void Cleanup()
        {
            lock (_syncRoot)
            {
                if (_workQueue != null) { _workQueue.Dispose(); _workQueue = null; }
                _workerThread = null;
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
