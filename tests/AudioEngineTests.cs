using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Runtime.InteropServices;
using EarGuard.Audio;
using EarGuard.Config;

namespace EarGuard.Tests
{
    public static class AudioEngineTests
    {
        public static void RunAll()
        {
            Console.WriteLine("[TEST] Running AudioEngineTests...");
            Test_ClampingDecision_TriggersOnlyWhenAppropriate();
            Test_ClampingDecision_IgnoresOwnContextGuid();
            Test_ClampingDecision_RespectsUnprotectedDevice();
            Test_ApplyVolumeCeiling_NeverRaisesVolume();
            Test_ApplyVolumeCeiling_LowersOnlyWhenAboveTarget();
            Test_ApplyVolumeCeiling_SkipsUnprotectedDevices();
            Test_SafePlugInLimit_OnlyEverLowersVolume();
            Test_SafePlugInWindow_HoldsLimitThenReleasesToCeiling();
            Test_ConcurrentClamps_DoNotRaiseHardware();
            Test_CallbackDuringWrite_DoesNotDeadlock();
            Test_FailedAttempts_DoNotInvalidateHealthyEndpoints();
            Test_InvalidatedAttempts_RemoveOnlyFailedEndpoint();
            Test_RapidReconnect_RenewsGraceWithoutAbsentScan();
            Test_HardwareNotifications_DoNotBlockQueuedWork();
            Test_RepeatedScans_DoNotRepublishUnchangedDevices();
            Test_DefaultResolutionFailure_ClearsPreviousId();
            Test_AudioVolumeNotificationData_StructSizeAndOffsets();
            Test_VolumeScalar_PercentageCalculations();
            Test_WatchdogInterval_ConfiguredTo100Milliseconds();
            Test_PowerBroadcastConstants_ValidValues();
            Test_RpcRecovery_DetectionLogic();
            Test_HandleSystemResume_LocksEndpointsToSafePlugInVolume();
            Test_MMCSS_WorkerThreadInitializes();
            Console.WriteLine("[PASS] All AudioEngineTests passed!");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Assertion Failed: " + message);
            }
        }

        private static void Test_ClampingDecision_TriggersOnlyWhenAppropriate()
        {
            Guid ourContext = Guid.NewGuid();
            Guid externalContext = Guid.NewGuid();

            // 1. Catastrophic Spike: volume 1.0f (100%), ceiling 0.30f (30%), enabled = true
            bool catastrophicSpike = AudioEngine.ShouldClampVolume(
                isDeviceEnabled: true,
                currentVolumeScalar: 1.0f,
                maxLimitScalar: 0.30f,
                eventContext: externalContext,
                engineContext: ourContext
            );
            Assert(catastrophicSpike == true, "100% Volume spike must trigger clamping on protected device");

            // 2. Subtle spike: volume 0.32f (32%), ceiling 0.30f (30%), enabled = true
            bool subtleSpike = AudioEngine.ShouldClampVolume(
                isDeviceEnabled: true,
                currentVolumeScalar: 0.32f,
                maxLimitScalar: 0.30f,
                eventContext: externalContext,
                engineContext: ourContext
            );
            Assert(subtleSpike == true, "Subtle 2% spike must trigger clamping on protected device");

            // 3. Normal volume: volume 0.20f (20%), ceiling 0.30f (30%), enabled = true
            bool normalVol = AudioEngine.ShouldClampVolume(
                isDeviceEnabled: true,
                currentVolumeScalar: 0.20f,
                maxLimitScalar: 0.30f,
                eventContext: externalContext,
                engineContext: ourContext
            );
            Assert(normalVol == false, "Normal volume under ceiling must not clamp");

            // 4. Exact boundary volume: 0.30f on 0.30f, enabled = true
            bool boundaryVol = AudioEngine.ShouldClampVolume(
                isDeviceEnabled: true,
                currentVolumeScalar: 0.30f,
                maxLimitScalar: 0.30f,
                eventContext: externalContext,
                engineContext: ourContext
            );
            Assert(boundaryVol == false, "Volume exactly at ceiling must not clamp");

            Console.WriteLine("  ✓ Test_ClampingDecision_TriggersOnlyWhenAppropriate");
        }

        private static void Test_ClampingDecision_IgnoresOwnContextGuid()
        {
            Guid ourContext = Guid.NewGuid();

            // Even if volume is 1.0f, if the event was triggered by our own ContextGuid, we must ignore it
            bool shouldClamp = AudioEngine.ShouldClampVolume(
                isDeviceEnabled: true,
                currentVolumeScalar: 1.0f,
                maxLimitScalar: 0.30f,
                eventContext: ourContext,
                engineContext: ourContext
            );
            Assert(shouldClamp == false, "Own context GUID must be ignored to prevent infinite feedback loops");
            Console.WriteLine("  ✓ Test_ClampingDecision_IgnoresOwnContextGuid");
        }

        private static void Test_ClampingDecision_RespectsUnprotectedDevice()
        {
            Guid ourContext = Guid.NewGuid();
            Guid externalContext = Guid.NewGuid();

            // When device is unprotected (isDeviceEnabled: false), spikes are NOT clamped
            bool shouldClamp = AudioEngine.ShouldClampVolume(
                isDeviceEnabled: false,
                currentVolumeScalar: 1.0f,
                maxLimitScalar: 0.30f,
                eventContext: externalContext,
                engineContext: ourContext
            );
            Assert(shouldClamp == false, "Unprotected device must not be clamped");
            Console.WriteLine("  ✓ Test_ClampingDecision_RespectsUnprotectedDevice");
        }

        private static void Test_ApplyVolumeCeiling_NeverRaisesVolume()
        {
            // CARDINAL RULE: EarGuard must never be able to raise volume, under any circumstance.
            // These cases cover every way a naive implementation could accidentally increase it.
            var cases = new[]
            {
                // current, ceiling, description
                new object[] { 0.05f, 0.60f, "raising the ceiling far above the current volume" },
                new object[] { 0.10f, 0.10f, "ceiling exactly equal to the current volume" },
                new object[] { 0.02f, 1.00f, "ceiling at 100%" },
                new object[] { 0.00f, 0.50f, "silent endpoint with a ceiling above it" },
                new object[] { 0.25f, 0.30f, "current volume already safely under the ceiling" }
            };

            foreach (var testCase in cases)
            {
                float current = (float)testCase[0];
                float ceiling = (float)testCase[1];
                string description = (string)testCase[2];

                var endpoint = new FakeEndpointVolume(current);
                var device = CreateGuardedDevice(endpoint, ceiling, enabled: true);
                var configStore = new ConfigStore();
                using (var engine = new AudioEngine(configStore, EarGuardConfig.CreateDefault()))
                {
                    var result = engine.ApplyVolumeCeiling(device, ceiling);

                    Assert(endpoint.WriteCount == 0,
                        "No hardware write may be issued when " + description);
                    Assert(endpoint.Volume >= current - 0.0001f,
                        "Hardware volume must not drop unexpectedly when " + description);
                    Assert(result.NewVolume <= result.OldVolume + 0.0001f,
                        "Result must never report a volume increase when " + description);
                }
            }

            Console.WriteLine("  ✓ Test_ApplyVolumeCeiling_NeverRaisesVolume");
        }

        private static void Test_ApplyVolumeCeiling_LowersOnlyWhenAboveTarget()
        {
            var endpoint = new FakeEndpointVolume(0.80f);
            var device = CreateGuardedDevice(endpoint, 0.30f, enabled: true);
            var configStore = new ConfigStore();
            using (var engine = new AudioEngine(configStore, EarGuardConfig.CreateDefault()))
            {
                var result = engine.ApplyVolumeCeiling(device, 0.30f);

                Assert(result.Outcome == VolumeAdjustmentOutcome.Lowered, "An 80% endpoint must be lowered to the 30% ceiling");
                Assert(endpoint.WriteCount == 1, "Exactly one downward write must be issued");
                Assert(Math.Abs(endpoint.Volume - 0.30f) < 0.0001f, "Hardware must end up at the ceiling");
                Assert(Math.Abs(result.OldVolume - 0.80f) < 0.0001f, "Result must report the pre-clamp volume");
                Assert(Math.Abs(result.NewVolume - 0.30f) < 0.0001f, "Result must report the clamped volume");
                Assert(endpoint.LastWritten <= endpoint.LastObservedBeforeWrite,
                    "The written value must never exceed what the hardware reported");

                // A second pass must be a no-op: the endpoint is now at the ceiling.
                var second = engine.ApplyVolumeCeiling(device, 0.30f);
                Assert(second.Outcome == VolumeAdjustmentOutcome.AlreadySafe, "Second pass must be a no-op");
                Assert(endpoint.WriteCount == 1, "No additional write may be issued once the endpoint is safe");
            }

            Console.WriteLine("  ✓ Test_ApplyVolumeCeiling_LowersOnlyWhenAboveTarget");
        }

        private static void Test_ApplyVolumeCeiling_SkipsUnprotectedDevices()
        {
            var endpoint = new FakeEndpointVolume(1.00f);
            var device = CreateGuardedDevice(endpoint, 0.30f, enabled: false);
            var configStore = new ConfigStore();
            using (var engine = new AudioEngine(configStore, EarGuardConfig.CreateDefault()))
            {
                var result = engine.ApplyVolumeCeiling(device, 0.30f);

                Assert(result.Outcome == VolumeAdjustmentOutcome.Skipped, "Unprotected devices must be skipped");
                Assert(endpoint.WriteCount == 0, "Unprotected devices must never be written to");
                Assert(Math.Abs(endpoint.Volume - 1.00f) < 0.0001f,
                    "Disabling protection must not change the endpoint volume in either direction");
            }

            Console.WriteLine("  ✓ Test_ApplyVolumeCeiling_SkipsUnprotectedDevices");
        }

        private static void Test_SafePlugInLimit_OnlyEverLowersVolume()
        {
            // The safe plug-in level is a CAP, not a target. A device that Windows hands us at 0%
            // must stay silent, and a device that comes up loud must be brought down to the cap.
            var silentEndpoint = new FakeEndpointVolume(0.00f);
            var silentDevice = CreateGuardedDevice(silentEndpoint, 0.30f, enabled: true);
            silentDevice.Config.SafePlugInVol = 0.05f;

            var loudEndpoint = new FakeEndpointVolume(0.80f);
            var loudDevice = CreateGuardedDevice(loudEndpoint, 0.30f, enabled: true);
            loudDevice.Config.SafePlugInVol = 0.05f;

            var configStore = new ConfigStore();
            using (var engine = new AudioEngine(configStore, EarGuardConfig.CreateDefault()))
            {
                var silentResult = engine.ApplyVolumeCeiling(silentDevice, silentDevice.Config.SafePlugInVol);
                Assert(silentResult.Outcome == VolumeAdjustmentOutcome.AlreadySafe,
                    "A device starting at 0% must not be raised to the plug-in limit");
                Assert(silentEndpoint.WriteCount == 0, "No write may be issued for an already-quiet device");
                Assert(Math.Abs(silentEndpoint.Volume - 0.00f) < 0.0001f,
                    "A device starting at 0% must remain silent when protection is applied");

                var loudResult = engine.ApplyVolumeCeiling(loudDevice, loudDevice.Config.SafePlugInVol);
                Assert(loudResult.Outcome == VolumeAdjustmentOutcome.Lowered,
                    "A device starting at 80% must be lowered to the plug-in limit");
                Assert(Math.Abs(loudEndpoint.Volume - 0.05f) < 0.0001f,
                    "A loud device must end up at the plug-in limit, not above it");
            }

            Console.WriteLine("  ✓ Test_SafePlugInLimit_OnlyEverLowersVolume");
        }

        private static void Test_SafePlugInWindow_HoldsLimitThenReleasesToCeiling()
        {
            var endpoint = new FakeEndpointVolume(0.20f);
            var device = CreateGuardedDevice(endpoint, 0.30f, enabled: true);
            device.SafePlugInUntilUtc = DateTime.MaxValue;
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            {
                AttachDevice(engine, device);
                InvokeEngine(engine, "WatchdogTick", (object)null);
                Assert(Math.Abs(endpoint.Volume - 0.05f) < 0.0001f,
                    "Watchdog must undo a restore below the ceiling during grace");
                endpoint.Volume = 0.20f;
                NotifyVolume(engine, device, endpoint.Volume);
                Assert(Math.Abs(endpoint.Volume - 0.05f) < 0.0001f,
                    "External callback must hold the plug-in cap during grace");

                device.SafePlugInUntilUtc = DateTime.MinValue;
                endpoint.Volume = 0.20f;
                int writes = endpoint.WriteCount;
                InvokeEngine(engine, "WatchdogTick", (object)null);
                NotifyVolume(engine, device, endpoint.Volume);
                Assert(endpoint.WriteCount == writes && endpoint.Volume == 0.20f,
                    "Both paths must release the plug-in cap after grace");
                endpoint.Volume = 0.80f;
                InvokeEngine(engine, "WatchdogTick", (object)null);
                Assert(Math.Abs(endpoint.Volume - 0.30f) < 0.0001f,
                    "Watchdog must still enforce the ceiling after grace");
            }
        }

        private static void Test_ConcurrentClamps_DoNotRaiseHardware()
        {
            var endpoint = new FakeEndpointVolume(0.80f);
            var device = CreateGuardedDevice(endpoint, 0.30f, true);
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            using (var firstRead = new ManualResetEvent(false))
            using (var releaseRead = new ManualResetEvent(false))
            using (var secondStarted = new ManualResetEvent(false))
            {
                int reads = 0;
                endpoint.AfterRead = () =>
                {
                    if (Interlocked.Increment(ref reads) == 1)
                    {
                        firstRead.Set();
                        if (!releaseRead.WaitOne(5000)) throw new TimeoutException("First reader was not released");
                    }
                };
                Exception firstError = null;
                Exception secondError = null;
                var first = new Thread(() =>
                {
                    try { engine.ApplyVolumeCeiling(device, 0.30f); }
                    catch (Exception ex) { firstError = ex; }
                }) { IsBackground = true };
                var second = new Thread(() =>
                {
                    secondStarted.Set();
                    try { engine.ApplyVolumeCeiling(device, 0.05f); }
                    catch (Exception ex) { secondError = ex; }
                }) { IsBackground = true };
                first.Start();
                try
                {
                    Assert(firstRead.WaitOne(5000), "First call must reach hardware read");
                    second.Start();
                    Assert(secondStarted.WaitOne(5000), "Second call must start");
                    // Wait for actual lock contention (fixed) or completion (racy), not a sleep.
                    Assert(SpinWait.SpinUntil(() => !second.IsAlive ||
                        (second.ThreadState & ThreadState.WaitSleepJoin) != 0, 5000),
                        "Second caller must complete or contend on the writer lock");
                }
                finally
                {
                    releaseRead.Set();
                    Assert(first.Join(5000), "First clamp must finish");
                    if ((second.ThreadState & ThreadState.Unstarted) == 0)
                        Assert(second.Join(5000), "Second clamp must finish");
                }
                Assert(firstError == null && secondError == null, "Concurrent clamps must succeed");
                Assert(endpoint.RaisingWriteCount == 0, "Every actual write must be at or below hardware immediately before it");
                Assert(Math.Abs(endpoint.Volume - 0.05f) < 0.0001f, "The stricter cap must survive concurrent calls");
            }
        }

        private static void Test_FailedAttempts_DoNotInvalidateHealthyEndpoints()
        {
            Action<FakeEndpointVolume>[] failures =
            {
                e => e.ReadHr = unchecked((int)0x80004005),
                e => e.ReadException = new InvalidOperationException("Read failed"),
                e => e.Volume = float.NaN,
                e => e.Volume = float.PositiveInfinity,
                e => e.WriteHr = unchecked((int)0x80004005),
                e => e.WriteException = new COMException("Write failed", unchecked((int)0x80004005))
            };
            foreach (var fail in failures)
            {
                var endpoint = new FakeEndpointVolume(0.80f);
                var device = CreateGuardedDevice(endpoint, 0.30f, true);
                using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
                {
                    AttachDevice(engine, device);
                    fail(endpoint);
                    var result = engine.ApplyVolumeCeiling(device, 0.30f);
                    InvokeEngine(engine, "WatchdogTick", (object)null);
                    Assert(result.Outcome == VolumeAdjustmentOutcome.Failed,
                        "Unknown hardware state must not be reported safe or invalidated");
                    Assert(engine.GuardedDevices.Count == 1 && device.VolumeControl == endpoint,
                        "Ordinary failures must leave the endpoint attached for watchdog retry");
                    Assert(device.SafePlugInUntilUtc == DateTime.MinValue,
                        "Ordinary failures must not restart plug-in grace");
                    Assert(endpoint.WriteCount == 0, "A failed attempt must not change hardware");
                    endpoint.ReadHr = endpoint.WriteHr = 0;
                    endpoint.ReadException = endpoint.WriteException = null;
                    endpoint.Volume = 0.80f;
                    InvokeEngine(engine, "WatchdogTick", (object)null);
                    Assert(endpoint.Volume == 0.30f, "The next watchdog tick must retry without reconnecting");
                }
            }
        }

        private static void Test_InvalidatedAttempts_RemoveOnlyFailedEndpoint()
        {
            Action<FakeEndpointVolume>[] failures =
            {
                e => e.ReadHr = CoreAudioConstants.RPC_E_DISCONNECTED,
                e => e.ReadException = new InvalidComObjectException(),
                e => e.WriteHr = unchecked((int)0x88890004),
                e => e.WriteException = new COMException("Disconnected", CoreAudioConstants.RPC_S_SERVER_UNAVAILABLE)
            };
            foreach (var fail in failures)
            {
                var endpoint = new FakeEndpointVolume(0.80f);
                var device = CreateGuardedDevice(endpoint, 0.30f, true);
                using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
                {
                    var queue = EnableControlledQueue(engine);
                    AttachDevice(engine, device);
                    fail(endpoint);
                    var result = engine.ApplyVolumeCeiling(device, 0.30f);
                    Assert(result.Outcome == VolumeAdjustmentOutcome.RecoveryRequested,
                        "Invalidation must request recovery");
                    Assert(engine.GuardedDevices.Count == 0 && device.VolumeControl == null,
                        "Invalidated endpoint must be detached");
                    Assert(endpoint.WriteCount == 0, "Invalidation must not commit a hardware change");
                    Action recovery;
                    Assert(queue.TryTake(out recovery), "Invalidation must enqueue recovery");
                    recovery();
                }
            }
        }

        private static void Test_RapidReconnect_RenewsGraceWithoutAbsentScan()
        {
            var endpoint = new FakeEndpointVolume(0.20f);
            var device = CreateGuardedDevice(endpoint, 0.30f, true);
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            {
                var queue = EnableControlledQueue(engine);
                AttachDevice(engine, device);
                var client = new AudioNotificationClient(() => InvokeEngine(engine, "OnHardwareDevicesChanged"));
                typeof(AudioNotificationClient).GetEvent("DeviceDisconnected", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetAddMethod(true).Invoke(client, new object[] { new Action<string>(id => InvokeEngine(engine, "OnDeviceDisconnected", id)) });
                client.OnDeviceStateChanged(device.DeviceId, 2);
                client.OnDeviceStateChanged(device.DeviceId, CoreAudioConstants.DEVICE_STATE_ACTIVE);
                Action transition;
                Assert(queue.TryTake(out transition), "Disconnect identity must be queued");
                transition();
                InvokeEngine(engine, "WatchdogTick", (object)null);
                Assert(endpoint.Volume == 0.05f && engine.GuardedDevices.Count == 1,
                    "Inactive/active transition must renew grace even while the endpoint remains attached");
            }
        }

        private static void Test_HardwareNotifications_DoNotBlockQueuedWork()
        {
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            {
                var queue = EnableControlledQueue(engine);
                InvokeEngine(engine, "OnHardwareDevicesChanged");
                using (var processed = new ManualResetEvent(false))
                {
                    queue.Add(() => processed.Set());
                    var worker = new Thread(() =>
                    {
                        Action action;
                        while (!processed.WaitOne(0) && queue.TryTake(out action)) action();
                    }) { IsBackground = true };
                    worker.Start();
                    bool immediate = processed.WaitOne(500);
                    Assert(worker.Join(5000), "Controlled queue must finish");
                    Assert(immediate, "A hardware notification must not impose the former 1.4-second sleep on later work");
                }
            }
        }

        /// <summary>
        /// Regression guard: a device-change notification used to re-scan on every 100 ms tick for
        /// the whole three-second grace window. Each scan ended by saving settings and raising
        /// DevicesChanged, which rebuilt the UI device list, so one notification cost roughly thirty
        /// scans and thirty settings writes. Repeated scans that find nothing new must stay silent.
        /// </summary>
        private static void Test_RepeatedScans_DoNotRepublishUnchangedDevices()
        {
            var endpoint = new FakeEndpointVolume(0.20f);
            var device = CreateGuardedDevice(endpoint, 0.30f, true);
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            {
                int published = 0;
                engine.DevicesChanged += (s, e) => Interlocked.Increment(ref published);

                var enumerator = new SingleDeviceEnumerator(device.DeviceId);
                SetEngineField(engine, "_enumerator", enumerator);
                var queue = EnableControlledQueue(engine);

                // First scan discovers the endpoint: that is a real change and must be published.
                engine.ScanDevices();
                Assert(published == 1, "Discovering an endpoint must publish a device change");

                // Re-scanning an unchanged device set must publish nothing: the caps are re-asserted
                // but the UI and settings file have nothing new to reflect.
                for (int i = 0; i < 10; i++) engine.ScanDevices();
                Assert(published == 1,
                    "Repeated scans of an unchanged device set must not republish (was " + published + ")");

                // A notification must still enqueue its discovery scan, and that scan must stay
                // silent too when the endpoint set has not changed.
                InvokeEngine(engine, "OnHardwareDevicesChanged");
                Action pending;
                Assert(queue.TryTake(out pending), "Notification must enqueue discovery work");
                pending();
                Assert(published == 1,
                    "The notification's own scan of an unchanged set must not republish");
                Assert(queue.Count == 0, "Retry ticks are timer-driven, not queued work");
            }
        }

        private sealed class SingleDeviceEnumerator : IMMDeviceEnumerator
        {
            private readonly string _deviceId;
            public SingleDeviceEnumerator(string deviceId) { _deviceId = deviceId; }
            public int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices)
            { devices = new SingleDeviceCollection(_deviceId); return 0; }
            public int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint)
            { endpoint = new SingleDevice(_deviceId); return 0; }
            public int GetDevice(string pwstrId, out IMMDevice endpoint) { endpoint = new SingleDevice(_deviceId); return 0; }
            public int RegisterEndpointNotificationCallback(IMMNotificationClient pClient) { return 0; }
            public int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient) { return 0; }
        }

        private sealed class SingleDeviceCollection : IMMDeviceCollection
        {
            private readonly string _deviceId;
            public SingleDeviceCollection(string deviceId) { _deviceId = deviceId; }
            public int GetCount(out uint count) { count = 1; return 0; }
            public int Item(uint index, out IMMDevice device) { device = new SingleDevice(_deviceId); return 0; }
        }

        private sealed class SingleDevice : IMMDevice
        {
            private readonly string _deviceId;
            public SingleDevice(string deviceId) { _deviceId = deviceId; }
            public int Activate(ref Guid id, int clsCtx, IntPtr activationParams, out object interfacePointer)
            { interfacePointer = new FakeEndpointVolume(0.20f); return 0; }
            public int OpenPropertyStore(int stgmAccess, out IPropertyStore properties)
            { properties = new EmptyPropertyStore(); return 0; }
            public int GetId(out string id) { id = _deviceId; return 0; }
            public int GetState(out int state) { state = CoreAudioConstants.DEVICE_STATE_ACTIVE; return 0; }
        }

        private sealed class EmptyPropertyStore : IPropertyStore
        {
            public int GetCount(out uint count) { count = 0; return 0; }
            public int GetAt(uint iProp, out PROPERTYKEY pkey) { pkey = new PROPERTYKEY(); return 0; }
            public int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv) { pv = new PROPVARIANT(); return unchecked((int)0x80070002); }
            public int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar) { return 0; }
            public int Commit() { return 0; }
        }

        private static void Test_CallbackDuringWrite_DoesNotDeadlock()
        {
            var endpoint = new FakeEndpointVolume(0.80f);
            var device = CreateGuardedDevice(endpoint, 0.30f, true);
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            {
                var queue = EnableControlledQueue(engine);
                bool callbackReturned = false;
                Thread callback = null;
                endpoint.BeforeWrite = () =>
                {
                    callback = new Thread(() => NotifyVolume(engine, device, 0.80f)) { IsBackground = true };
                    callback.Start();
                    callbackReturned = callback.Join(2000);
                };
                engine.ApplyVolumeCeiling(device, 0.30f);
                Assert(callback != null && callback.Join(5000), "Callback must eventually finish");
                Assert(callbackReturned, "COM callback must return while the setter holds the writer lock");
                endpoint.BeforeWrite = null;
                Action deferred;
                Assert(queue.TryTake(out deferred), "Contended callback must be deferred rather than dropped");
                deferred();
                Assert(endpoint.WriteCount == 1 && endpoint.RaisingWriteCount == 0,
                    "Deferred callback must re-read hardware rather than restore its stale notification volume");
            }
        }

        private static void Test_DefaultResolutionFailure_ClearsPreviousId()
        {
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            {
                SetEngineField(engine, "_defaultDeviceId", "STALE-ENDPOINT");
                InvokeEngine(engine, "RefreshDefaultDeviceId");
                Assert(engine.DefaultDeviceId == string.Empty, "Unavailable enumerator must not retain the old default");
                var enumerator = new FailingDefaultEnumerator();
                SetEngineField(engine, "_enumerator", enumerator);
                SetEngineField(engine, "_defaultDeviceId", "STALE-ENDPOINT");
                InvokeEngine(engine, "RefreshDefaultDeviceId");
                Assert(engine.DefaultDeviceId == string.Empty, "Failed default HRESULT must clear the old identity");
                enumerator.ThrowOnDefault = true;
                SetEngineField(engine, "_defaultDeviceId", "STALE-ENDPOINT");
                InvokeEngine(engine, "RefreshDefaultDeviceId");
                Assert(engine.DefaultDeviceId == string.Empty, "Default resolution exception must clear the old identity");
            }
        }

        private sealed class FailingDefaultEnumerator : IMMDeviceEnumerator
        {
            public bool ThrowOnDefault;
            public int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint)
            {
                endpoint = null;
                if (ThrowOnDefault) throw new COMException("Default unavailable");
                return unchecked((int)0x80004005);
            }
            public int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices) { devices = null; return 0; }
            public int GetDevice(string id, out IMMDevice endpoint) { endpoint = null; return 0; }
            public int RegisterEndpointNotificationCallback(IMMNotificationClient client) { return 0; }
            public int UnregisterEndpointNotificationCallback(IMMNotificationClient client) { return 0; }
        }

        private static void AttachDevice(AudioEngine engine, GuardedDevice device)
        {
            var devices = (List<GuardedDevice>)typeof(AudioEngine).GetField("_guardedDevices", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            devices.Add(device);
        }

        private static void SetEngineField(AudioEngine engine, string name, object value)
        {
            typeof(AudioEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine, value);
        }

        private static BlockingCollection<Action> EnableControlledQueue(AudioEngine engine)
        {
            var queue = new BlockingCollection<Action>();
            SetEngineField(engine, "_workQueue", queue);
            SetEngineField(engine, "_workerRunning", true);
            return queue;
        }

        private static void InvokeEngine(AudioEngine engine, string name, params object[] args)
        {
            typeof(AudioEngine).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine, args);
        }

        private static void NotifyVolume(AudioEngine engine, GuardedDevice device, float volume)
        {
            var callback = new AudioEndpointCallback(data => InvokeEngine(engine, "HandleVolumeNotification", device, data));
            IntPtr notification = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AUDIO_VOLUME_NOTIFICATION_DATA)));
            try
            {
                Marshal.StructureToPtr(new AUDIO_VOLUME_NOTIFICATION_DATA
                {
                    fMasterVolume = volume,
                    guidEventContext = Guid.Empty
                }, notification, false);
                callback.OnNotify(notification);
            }
            finally
            {
                Marshal.FreeHGlobal(notification);
            }
        }

        private static GuardedDevice CreateGuardedDevice(IAudioEndpointVolume endpoint, float maxLimit, bool enabled)
        {
            return new GuardedDevice
            {
                DeviceId = "TEST-DEVICE",
                DeviceName = "Test Endpoint",
                VolumeControl = endpoint,
                Config = new DeviceConfig
                {
                    DeviceId = "TEST-DEVICE",
                    DeviceName = "Test Endpoint",
                    Enabled = enabled,
                    MaxLimit = maxLimit,
                    SafePlugInVol = 0.05f
                }
            };
        }

        /// <summary>
        /// In-memory stand-in for a hardware endpoint. It records every write so the tests can prove
        /// that EarGuard only ever issues a write when the endpoint got too loud.
        /// </summary>
        private sealed class FakeEndpointVolume : IAudioEndpointVolume
        {
            public float Volume;
            public int WriteCount;
            public float LastWritten;
            public float LastObservedBeforeWrite;
            public int RaisingWriteCount;
            public int ReadHr;
            public int WriteHr;
            public Exception ReadException;
            public Exception WriteException;
            public Action AfterRead;
            public Action BeforeWrite;

            public FakeEndpointVolume(float initialVolume)
            {
                Volume = initialVolume;
            }

            public int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify) { return 0; }
            public int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify) { return 0; }
            public int GetChannelCount(out uint pnChannelCount) { pnChannelCount = 2; return 0; }
            public int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext) { return 0; }

            public int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext)
            {
                if (WriteException != null) throw WriteException;
                if (WriteHr != 0) return WriteHr;
                if (BeforeWrite != null) BeforeWrite();
                if (fLevel > Volume) RaisingWriteCount++;
                LastObservedBeforeWrite = Volume;
                LastWritten = fLevel;
                WriteCount++;
                Volume = fLevel;
                return 0;
            }

            public int GetMasterVolumeLevel(out float pfLevelDB) { pfLevelDB = 0f; return 0; }
            public int GetMasterVolumeLevelScalar(out float pfLevel)
            {
                if (ReadException != null) throw ReadException;
                pfLevel = Volume;
                if (AfterRead != null) AfterRead();
                return ReadHr;
            }
            public int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext) { return 0; }
            public int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext) { return 0; }
            public int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB) { pfLevelDB = 0f; return 0; }
            public int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel) { pfLevel = Volume; return 0; }
            public int SetMute(bool bMute, ref Guid pguidEventContext) { return 0; }
            public int GetMute(out bool pbMute) { pbMute = false; return 0; }
        }

        private static void Test_AudioVolumeNotificationData_StructSizeAndOffsets()
        {
            // Verify AUDIO_VOLUME_NOTIFICATION_DATA layout
            int size = Marshal.SizeOf(typeof(AUDIO_VOLUME_NOTIFICATION_DATA));
            Assert(size >= 24, "AUDIO_VOLUME_NOTIFICATION_DATA must be at least 24 bytes");

            int guidOffset = (int)Marshal.OffsetOf(typeof(AUDIO_VOLUME_NOTIFICATION_DATA), "guidEventContext");
            Assert(guidOffset == 0, "guidEventContext must be at offset 0");

            int volOffset = (int)Marshal.OffsetOf(typeof(AUDIO_VOLUME_NOTIFICATION_DATA), "fMasterVolume");
            Assert(volOffset > 0, "fMasterVolume must have positive offset");
            Console.WriteLine("  ✓ Test_AudioVolumeNotificationData_StructSizeAndOffsets");
        }

        private static void Test_VolumeScalar_PercentageCalculations()
        {
            Assert(AudioEngine.ScalarToPercent(0.0f) == 0, "0.0 scalar must be 0%");
            Assert(AudioEngine.ScalarToPercent(0.05f) == 5, "0.05 scalar must be 5%");
            Assert(AudioEngine.ScalarToPercent(0.30f) == 30, "0.30 scalar must be 30%");
            Assert(AudioEngine.ScalarToPercent(1.0f) == 100, "1.0 scalar must be 100%");

            Assert(Math.Abs(AudioEngine.PercentToScalar(0) - 0.0f) < 0.001f, "0% must be 0.0f scalar");
            Assert(Math.Abs(AudioEngine.PercentToScalar(5) - 0.05f) < 0.001f, "5% must be 0.05f scalar");
            Assert(Math.Abs(AudioEngine.PercentToScalar(30) - 0.30f) < 0.001f, "30% must be 0.30f scalar");
            Assert(Math.Abs(AudioEngine.PercentToScalar(100) - 1.0f) < 0.001f, "100% must be 1.0f scalar");
            Console.WriteLine("  ✓ Test_VolumeScalar_PercentageCalculations");
        }

        private static void Test_WatchdogInterval_ConfiguredTo100Milliseconds()
        {
            Assert(AudioEngine.WatchdogIntervalMs == 100, "Watchdog interval must be exactly 100ms");
            Console.WriteLine("  ✓ Test_WatchdogInterval_ConfiguredTo100Milliseconds");
        }

        private static void Test_PowerBroadcastConstants_ValidValues()
        {
            Assert(CoreAudioConstants.WM_POWERBROADCAST == 0x0218, "WM_POWERBROADCAST must be 0x0218");
            Assert(CoreAudioConstants.PBT_APMRESUMEAUTOMATIC == 0x0012, "PBT_APMRESUMEAUTOMATIC must be 0x0012");
            Assert(CoreAudioConstants.PBT_APMRESUMESUSPEND == 0x0007, "PBT_APMRESUMESUSPEND must be 0x0007");
            Assert(CoreAudioConstants.RPC_S_SERVER_UNAVAILABLE == unchecked((int)0x800706BA), "RPC_S_SERVER_UNAVAILABLE must be 0x800706BA");
            Assert(CoreAudioConstants.RPC_E_DISCONNECTED == unchecked((int)0x80010108), "RPC_E_DISCONNECTED must be 0x80010108");
            Console.WriteLine("  ✓ Test_PowerBroadcastConstants_ValidValues");
        }

        private static void Test_RpcRecovery_DetectionLogic()
        {
            Assert(AudioEngine.IsRpcErrorCode(CoreAudioConstants.RPC_S_SERVER_UNAVAILABLE), "RPC_S_SERVER_UNAVAILABLE must be recognized as RPC error");
            Assert(AudioEngine.IsRpcErrorCode(CoreAudioConstants.RPC_E_DISCONNECTED), "RPC_E_DISCONNECTED must be recognized as RPC error");
            Assert(AudioEngine.IsRpcErrorCode(unchecked((int)0x88890004)), "AUDCLNT_E_DEVICE_INVALIDATED must be recognized as RPC error");
            Assert(AudioEngine.IsRpcErrorCode(unchecked((int)0x80070490)), "ERROR_NOT_FOUND must be recognized as RPC error");
            Assert(!AudioEngine.IsRpcErrorCode(0), "S_OK (0) must not be recognized as RPC error");

            var rpcEx = new COMException("RPC Server Unavailable", CoreAudioConstants.RPC_S_SERVER_UNAVAILABLE);
            Assert(AudioEngine.IsRpcOrComException(rpcEx), "COMException with RPC_S_SERVER_UNAVAILABLE must be detected");

            var discEx = new COMException("Disconnected", CoreAudioConstants.RPC_E_DISCONNECTED);
            Assert(AudioEngine.IsRpcOrComException(discEx), "COMException with RPC_E_DISCONNECTED must be detected");

            var invalidComEx = new InvalidComObjectException("Invalid COM");
            Assert(AudioEngine.IsRpcOrComException(invalidComEx), "InvalidComObjectException must be detected");

            var normalEx = new InvalidOperationException("Normal exception");
            Assert(!AudioEngine.IsRpcOrComException(normalEx), "InvalidOperationException must not be detected as RPC exception");
            Console.WriteLine("  ✓ Test_RpcRecovery_DetectionLogic");
        }

        private static void Test_HandleSystemResume_LocksEndpointsToSafePlugInVolume()
        {
            var endpoint = new FakeEndpointVolume(0.80f);
            var surviving = CreateGuardedDevice(endpoint, 0.30f, true);
            var failedEndpoint = new FakeEndpointVolume(0.80f) { ReadHr = CoreAudioConstants.RPC_E_DISCONNECTED };
            var invalidated = CreateGuardedDevice(failedEndpoint, 0.30f, true);
            invalidated.DeviceId = "INVALIDATED";
            using (var engine = new AudioEngine(new ConfigStore(), EarGuardConfig.CreateDefault()))
            {
                // Reverse traversal visits the invalidated endpoint first, then the survivor.
                AttachDevice(engine, surviving);
                AttachDevice(engine, invalidated);
                engine.HandleSystemResume();
                Assert(engine.GuardedDevices.Count == 1 && engine.GuardedDevices[0] == surviving,
                    "Resume must remove invalidated endpoint without skipping surviving endpoint");
                Assert(endpoint.Volume == 0.05f, "Resume must immediately clamp the surviving endpoint");
                endpoint.Volume = 0.20f;
                InvokeEngine(engine, "WatchdogTick", (object)null);
                Assert(endpoint.Volume == 0.05f, "Resume must renew expired grace for watchdog restores");
                endpoint.Volume = 0.20f;
                NotifyVolume(engine, surviving, endpoint.Volume);
                Assert(endpoint.Volume == 0.05f, "Resume must renew expired grace for callback restores");
            }
        }

        private static void Test_MMCSS_WorkerThreadInitializes()
        {
            var configStore = new ConfigStore();
            var config = EarGuardConfig.CreateDefault();
            using (var engine = new AudioEngine(configStore, config))
            {
                engine.Start();
                // Give the worker thread a moment to initialize MMCSS
                System.Threading.Thread.Sleep(50);
                Assert(engine.IsMmcssActive, "MMCSS 'Pro Audio' task must be registered and active on the worker thread");
            }
            Console.WriteLine("  ✓ Test_MMCSS_WorkerThreadInitializes");
        }
    }
}
