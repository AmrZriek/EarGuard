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

    public class AudioEngine : IDisposable
    {
        public static readonly Guid ContextGuid = Guid.NewGuid();
        public const int WatchdogIntervalMs = 100;

        private readonly ConfigStore _configStore;
        private readonly EarGuardConfig _config;
        private readonly object _syncRoot = new object();
        private readonly List<GuardedDevice> _guardedDevices = new List<GuardedDevice>();
        private readonly HashSet<string> _seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IMMDeviceEnumerator _enumerator;
        private AudioNotificationClient _notificationClient;
        private Timer _watchdogTimer;
        private Thread _workerThread;
        private BlockingCollection<Action> _workQueue;
        private volatile bool _workerRunning;
        private IntPtr _mmcssHandle = IntPtr.Zero;
        private int _resyncPending;
        private bool _isDisposed;

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
                            Marshal.ReleaseComObject(dev);
                            continue;
                        }

                        currentActiveIds.Add(devId);

                        // Check if already hooked
                        var existing = _guardedDevices.Find(d => string.Equals(d.DeviceId, devId, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            Marshal.ReleaseComObject(dev);
                            if (existing.VolumeControl != null && existing.Config != null && existing.Config.Enabled)
                            {
                                try
                                {
                                    float existingVol = 0f;
                                    int ehr = existing.VolumeControl.GetMasterVolumeLevelScalar(out existingVol);
                                    if (ehr == 0)
                                    {
                                        existing.CurrentVolume = existingVol;
                                        if (existingVol > existing.Config.MaxLimit + 0.0001f)
                                        {
                                            Guid ctx = ContextGuid;
                                            existing.VolumeControl.SetMasterVolumeLevelScalar(existing.Config.MaxLimit, ref ctx);
                                            float oldVol = existing.CurrentVolume;
                                            existing.CurrentVolume = existing.Config.MaxLimit;
                                            RaiseVolumeClamped(existing, oldVol, existing.Config.MaxLimit);
                                        }
                                    }
                                    else if (IsRpcErrorCode(ehr))
                                    {
                                        TriggerFastResync(existing.DeviceId);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    if (IsRpcOrComException(ex))
                                    {
                                        TriggerFastResync(existing.DeviceId);
                                    }
                                }
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
                            Marshal.ReleaseComObject(dev);
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

                        // Query initial volume
                        float curVol = 0f;
                        int ghr = vol.GetMasterVolumeLevelScalar(out curVol);
                        if (ghr == 0)
                        {
                            guarded.CurrentVolume = curVol;
                        }


                        // Fresh plug-in enforcement: if newly connected, apply safe plug-in volume
                        bool isNewDevice = !_seenDeviceIds.Contains(devId);
                        _seenDeviceIds.Add(devId);

                        if (isNewDevice)
                        {
                            if (devConfig.Enabled)
                            {
                                try
                                {
                                    Guid ctx = ContextGuid;
                                    vol.SetMasterVolumeLevelScalar(devConfig.SafePlugInVol, ref ctx);
                                    guarded.CurrentVolume = devConfig.SafePlugInVol;
                                }
                                catch { }
                            }
                        }
                        else if (ShouldClampVolume(devConfig.Enabled, curVol, devConfig.MaxLimit, Guid.Empty, ContextGuid))
                        {
                            // Initial volume exceeds ceiling, clamp immediately!
                            try
                            {
                                Guid ctx = ContextGuid;
                                vol.SetMasterVolumeLevelScalar(devConfig.MaxLimit, ref ctx);
                                guarded.CurrentVolume = devConfig.MaxLimit;
                            }
                            catch { }
                        }

                        // Register COM volume callback
                        var callback = new AudioEndpointCallback(data => HandleVolumeNotification(guarded, data));
                        guarded.CallbackInstance = callback;
                        vol.RegisterControlChangeNotify(callback);

                        _guardedDevices.Add(guarded);
                    }

                    // Remove any devices that were disconnected
                    for (int i = _guardedDevices.Count - 1; i >= 0; i--)
                    {
                        var g = _guardedDevices[i];
                        if (!currentActiveIds.Contains(g.DeviceId))
                        {
                            g.Dispose();
                            _guardedDevices.RemoveAt(i);
                        }
                    }
                }
                finally
                {
                    if (col != null)
                    {
                        Marshal.ReleaseComObject(col);
                    }
                }

                _configStore.Save(_config);
                RaiseDevicesChanged();
            }
        }

        private void OnHardwareDevicesChanged()
        {
            // Scan immediately so a newly enumerated endpoint is clamped before playback.
            // Retry once after AudioSrv finishes any USB re-indexing race.
            PostWork(() =>
            {
                ScanDevices();
                Thread.Sleep(100);
                ScanDevices();
            });
        }

        private void HandleVolumeNotification(GuardedDevice guarded, AUDIO_VOLUME_NOTIFICATION_DATA data)
        {
            if (_isDisposed || guarded == null || guarded.Config == null) return;

            // Prioritize incoming COM callback thread under Pro-Audio MMCSS
            EnsureThreadMmcss();

            float newVol = data.fMasterVolume;

            // Check if this event was initiated by our own clamp
            if (data.guidEventContext == ContextGuid)
            {
                guarded.CurrentVolume = newVol;
                RaiseVolumeChanged(guarded);
                return;
            }

            bool shouldClamp = ShouldClampVolume(
                guarded.Config.Enabled,
                newVol,
                guarded.Config.MaxLimit,
                data.guidEventContext,
                ContextGuid
            );
            if (shouldClamp)
            {
                // Immediate clamp!
                try
                {
                    Guid ctx = ContextGuid;
                    int hr = guarded.VolumeControl.SetMasterVolumeLevelScalar(guarded.Config.MaxLimit, ref ctx);
                    if (hr == 0)
                    {
                        guarded.CurrentVolume = guarded.Config.MaxLimit;
                        guarded.IsClampedAlert = true;
                        RaiseVolumeClamped(guarded, newVol, guarded.Config.MaxLimit);
                    }
                    else if (IsRpcErrorCode(hr))
                    {
                        TriggerFastResync(guarded.DeviceId);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Error during volume clamp: " + ex.Message);
                    if (IsRpcOrComException(ex))
                    {
                        TriggerFastResync(guarded.DeviceId);
                    }
                }
            }
            else
            {
                guarded.CurrentVolume = newVol;
            }

            RaiseVolumeChanged(guarded);
        }

        private void WatchdogTick(object state)
        {
            if (_isDisposed) return;

            lock (_syncRoot)
            {
                string deadDeviceId = null;

                foreach (var guarded in _guardedDevices)
                {
                    if (guarded.VolumeControl == null || guarded.Config == null || !guarded.Config.Enabled)
                        continue;

                    try
                    {
                        float current = 0f;
                        int hr = guarded.VolumeControl.GetMasterVolumeLevelScalar(out current);
                        if (hr == 0)
                        {
                            guarded.CurrentVolume = current;
                            if (current > guarded.Config.MaxLimit + 0.001f)
                            {
                                Guid ctx = ContextGuid;
                                int shr = guarded.VolumeControl.SetMasterVolumeLevelScalar(guarded.Config.MaxLimit, ref ctx);
                                if (shr == 0)
                                {
                                    guarded.CurrentVolume = guarded.Config.MaxLimit;
                                    RaiseVolumeClamped(guarded, current, guarded.Config.MaxLimit);
                                }
                                else if (IsRpcErrorCode(shr))
                                {
                                    deadDeviceId = guarded.DeviceId;
                                    break;
                                }
                            }
                        }
                        else if (IsRpcErrorCode(hr))
                        {
                            deadDeviceId = guarded.DeviceId;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (IsRpcOrComException(ex))
                        {
                            deadDeviceId = guarded.DeviceId;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(deadDeviceId))
                {
                    TriggerFastResync(deadDeviceId);
                }
            }
        }

        public void HandleSystemResume()
        {
            lock (_syncRoot)
            {
                if (_isDisposed) return;

                // Instant high-priority clamp on all currently active endpoints
                foreach (var guarded in _guardedDevices)
                {
                    if (guarded.VolumeControl == null || guarded.Config == null || !guarded.Config.Enabled)
                        continue;

                    try
                    {
                        float target = Math.Min(guarded.Config.SafePlugInVol, guarded.Config.MaxLimit);
                        Guid ctx = ContextGuid;
                        int hr = guarded.VolumeControl.SetMasterVolumeLevelScalar(target, ref ctx);
                        if (hr == 0)
                        {
                            float oldVol = guarded.CurrentVolume;
                            guarded.CurrentVolume = target;
                            if (oldVol > target + 0.001f)
                            {
                                RaiseVolumeClamped(guarded, oldVol, target);
                            }
                        }
                        else if (IsRpcErrorCode(hr))
                        {
                            TriggerFastResync(guarded.DeviceId);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (IsRpcOrComException(ex))
                        {
                            TriggerFastResync(guarded.DeviceId);
                        }
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
                        Thread.Sleep(50);
                        ScanDevices();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _resyncPending, 0);
                    }
                });
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

        public void TestClamp(GuardedDevice device)
        {
            if (device == null || device.VolumeControl == null || device.Config == null) return;

            // Strictly spike ONLY 2% above the device limit (e.g. 30% -> 32%).
            // Never jump to 100%! This safely exercises the COM callback without any ear trauma.
            float targetSpike = Math.Min(1.0f, device.Config.MaxLimit + 0.02f);
            Guid testGuid = Guid.NewGuid();
            try
            {
                device.VolumeControl.SetMasterVolumeLevelScalar(targetSpike, ref testGuid);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Test clamp error: " + ex.Message);
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
