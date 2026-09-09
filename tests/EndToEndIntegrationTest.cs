using System;
using EarGuard.Audio;
using EarGuard.Config;

namespace EarGuard.Tests
{
    public static class EndToEndIntegrationTest
    {
        public static void Run()
        {
            Console.WriteLine("[TEST] Running EndToEndIntegrationTest on real hardware...");

            var configStore = new ConfigStore();
            var config = EarGuardConfig.CreateDefault();
            var engine = new AudioEngine(configStore, config);
            engine.DevicesChanged += (s, e) =>
            {
                Console.WriteLine("    [Event] DevicesChanged event triggered.");
            };

            engine.Start();

            var devices = engine.GuardedDevices;
            Console.WriteLine("  Active render endpoints detected: " + devices.Count);

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

            engine.Dispose();
            Console.WriteLine("[PASS] EndToEndIntegrationTest passed on real hardware!");
        }
    }
}
