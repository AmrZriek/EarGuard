using System;
using System.Threading;
using EarGuard.Audio;
using EarGuard.Config;

namespace EarGuard.Tests
{
    public static class EndToEndIntegrationTest
    {
        public static void Run()
        {
            Console.WriteLine("[TEST] Running EndToEndIntegrationTest on real hardware...");

            var configStore = TestEnvironment.Store();
            var config = EarGuardConfig.CreateDefault();
            using (var discovered = new ManualResetEvent(false))
            using (var engine = new AudioEngine(configStore, config))
            {
            bool succeeded = false;
            engine.DiscoveryCompleted += success =>
            {
                succeeded = success;
                discovered.Set();
            };
            engine.DevicesChanged += (s, e) =>
            {
                Console.WriteLine("    [Event] DevicesChanged event triggered.");
            };

            engine.Start();
            if (!discovered.WaitOne(10000)) throw new Exception("Hardware discovery did not complete within 10 seconds");
            if (!succeeded) throw new Exception("Hardware discovery attempt failed");

            var devices = engine.GuardedDevices;
            Console.WriteLine("  Active render endpoints detected: " + devices.Count);
            if (devices.Count == 0)
            {
                Console.WriteLine("[SKIP] No active render endpoints; hardware protection was not verified.");
                return;
            }

            foreach (var dev in devices)
            {
                Console.WriteLine(string.Format("    - Device: \"{0}\"", dev.DisplayName));
                Console.WriteLine(string.Format("      ID: {0}", dev.DeviceId));
                Console.WriteLine(string.Format("      Current Volume: {0}%", dev.CurrentVolumePercent));
                Console.WriteLine(string.Format("      Max Ceiling: {0}%, Safe Plug-in: {1}%", dev.MaxLimitPercent, dev.SafePlugInVolPercent));

                if (dev.VolumeControl == null)
                {
                    throw new Exception("Device VolumeControl COM pointer must not be null");
                }
                if (dev.CallbackInstance == null)
                {
                    throw new Exception("Device CallbackInstance must not be null");
                }
            }

            Console.WriteLine("[PASS] EndToEndIntegrationTest passed on real hardware!");
            }
        }
    }
}
