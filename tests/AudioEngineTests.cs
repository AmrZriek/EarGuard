using System;
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
            Test_SafeSpikeClamp_ExercisesTwoPercentOverCeiling();
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

        private static void Test_SafeSpikeClamp_ExercisesTwoPercentOverCeiling()
        {
            float ceiling = 0.30f;
            float safeTestSpike = Math.Min(1.0f, ceiling + 0.02f);
            Assert(Math.Abs(safeTestSpike - 0.32f) < 0.001f, "Test clamp spike must be strictly 2% above ceiling");

            Guid ourContext = Guid.NewGuid();
            Guid testSpikeGuid = Guid.NewGuid();

            bool triggers = AudioEngine.ShouldClampVolume(true, safeTestSpike, ceiling, testSpikeGuid, ourContext);
            Assert(triggers == true, "2% test clamp spike must successfully trigger clamp decision");
            Console.WriteLine("  ✓ Test_SafeSpikeClamp_ExercisesTwoPercentOverCeiling");
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
            var configStore = new ConfigStore();
            var config = EarGuardConfig.CreateDefault();
            using (var engine = new AudioEngine(configStore, config))
            {
                engine.Start();

                // Trigger system resume event
                engine.HandleSystemResume();

                // Verify each active guarded endpoint is clamped to its SafePlugInVol (default 5%)
                foreach (var device in engine.GuardedDevices)
                {
                    if (device.Config != null && device.Config.Enabled)
                    {
                        int currentPct = AudioEngine.ScalarToPercent(device.CurrentVolume);
                        int safePlugInPct = AudioEngine.ScalarToPercent(device.Config.SafePlugInVol);
                        Assert(currentPct <= safePlugInPct + 1,
                            string.Format("Device {0} volume ({1}%) must be at or below safe plug-in volume ({2}%) after resume",
                                device.DisplayName, currentPct, safePlugInPct));
                    }
                }
            }
            Console.WriteLine("  ✓ Test_HandleSystemResume_LocksEndpointsToSafePlugInVolume");
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
