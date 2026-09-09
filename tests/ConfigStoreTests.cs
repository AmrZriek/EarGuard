using System;
using System.IO;
using EarGuard.Config;

namespace EarGuard.Tests
{
    public static class ConfigStoreTests
    {
        public static void RunAll()
        {
            Console.WriteLine("[TEST] Running ConfigStoreTests...");
            Test_DefaultConfig_HasSafeInvariants();
            Test_SerializationAndDeserialization_RoundTrips();
            Test_Validation_ClampsOutOfRangeValues();
            Test_CorruptJson_RecoversWithDefaults();
            Test_GetOrCreateDeviceConfig_AddsAndRetrieves();
            Test_SilentLaunchArgs_DetectedCorrectly();
            Console.WriteLine("[PASS] All ConfigStoreTests passed!");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Assertion Failed: " + message);
            }
        }

        private static void Test_DefaultConfig_HasSafeInvariants()
        {
            var config = EarGuardConfig.CreateDefault();
            Assert(Math.Abs(config.GlobalMaxLimit - 0.30f) < 0.001f, "Default GlobalMaxLimit must be 30%");
            Assert(Math.Abs(config.GlobalSafePlugInVol - 0.05f) < 0.001f, "Default GlobalSafePlugInVol must be 5%");
            Assert(config.ShowNotificationOnBlock == true, "Default ShowNotificationOnBlock must be true");
            Assert(config.LaunchOnStartup == false, "Default LaunchOnStartup must be false");
            Assert(config.Devices != null, "Default Devices must not be null");
            Console.WriteLine("  ✓ Test_DefaultConfig_HasSafeInvariants");
        }

        private static void Test_SerializationAndDeserialization_RoundTrips()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "EarGuard_Test_RoundTrip_" + Guid.NewGuid() + ".json");
            try
            {
                var store = new ConfigStore();
                var original = new EarGuardConfig
                {
                    GlobalMaxLimit = 0.42f,
                    GlobalSafePlugInVol = 0.08f,
                    LaunchOnStartup = true,
                    ShowNotificationOnBlock = false
                };
                original.Devices.Add(new DeviceConfig
                {
                    DeviceId = "DEV-1234",
                    DeviceName = "Apple USB-C Dongle",
                    MaxLimit = 0.25f,
                    SafePlugInVol = 0.04f
                });

                store.Save(original, tempFile);
                Assert(File.Exists(tempFile), "Saved config file must exist");

                var loaded = store.Load(tempFile);
                Assert(Math.Abs(loaded.GlobalMaxLimit - 0.42f) < 0.001f, "Loaded GlobalMaxLimit mismatch");
                Assert(Math.Abs(loaded.GlobalSafePlugInVol - 0.08f) < 0.001f, "Loaded GlobalSafePlugInVol mismatch");
                Assert(loaded.LaunchOnStartup == true, "Loaded LaunchOnStartup mismatch");
                Assert(loaded.ShowNotificationOnBlock == false, "Loaded ShowNotificationOnBlock mismatch");
                Assert(loaded.Devices.Count == 1, "Loaded Devices count mismatch");
                Assert(loaded.Devices[0].DeviceId == "DEV-1234", "Loaded DeviceId mismatch");
                Assert(loaded.Devices[0].DeviceName == "Apple USB-C Dongle", "Loaded DeviceName mismatch");
                Assert(Math.Abs(loaded.Devices[0].MaxLimit - 0.25f) < 0.001f, "Loaded Device MaxLimit mismatch");
                Assert(Math.Abs(loaded.Devices[0].SafePlugInVol - 0.04f) < 0.001f, "Loaded Device SafePlugInVol mismatch");
                Console.WriteLine("  ✓ Test_SerializationAndDeserialization_RoundTrips");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private static void Test_Validation_ClampsOutOfRangeValues()
        {
            var store = new ConfigStore();
            var config = new EarGuardConfig
            {
                GlobalMaxLimit = 1.50f, // above 1.0
                GlobalSafePlugInVol = -0.10f // below 0
            };
            config.Devices.Add(new DeviceConfig
            {
                DeviceId = "DEV-OVER",
                DeviceName = "Over limit test",
                MaxLimit = 0.00f, // below 0.01
                SafePlugInVol = 0.80f // above MaxLimit
            });

            store.EnsureValid(config);

            Assert(config.GlobalMaxLimit <= 1.0f, "GlobalMaxLimit must be clamped <= 1.0");
            Assert(config.GlobalSafePlugInVol >= 0.0f, "GlobalSafePlugInVol must be clamped >= 0.0");
            Assert(config.Devices[0].MaxLimit >= 0.01f, "Device MaxLimit must be clamped >= 0.01");
            Assert(config.Devices[0].SafePlugInVol <= config.Devices[0].MaxLimit, "SafePlugInVol cannot exceed MaxLimit");
            Console.WriteLine("  ✓ Test_Validation_ClampsOutOfRangeValues");
        }

        private static void Test_CorruptJson_RecoversWithDefaults()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "EarGuard_Test_Corrupt_" + Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(tempFile, "{ broken json ::: corrupt 1234 }");
                var store = new ConfigStore();
                var loaded = store.Load(tempFile);
                Assert(loaded != null, "Corrupt file should return default config, not null");
                Assert(Math.Abs(loaded.GlobalMaxLimit - 0.30f) < 0.001f, "Default GlobalMaxLimit must be restored");
                Assert(Math.Abs(loaded.GlobalSafePlugInVol - 0.05f) < 0.001f, "Default GlobalSafePlugInVol must be restored");
                Console.WriteLine("  ✓ Test_CorruptJson_RecoversWithDefaults");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private static void Test_GetOrCreateDeviceConfig_AddsAndRetrieves()
        {
            var store = new ConfigStore();
            var config = EarGuardConfig.CreateDefault();

            var dev1 = store.GetOrCreateDeviceConfig(config, "DEV-A", "Headphones");
            Assert(dev1 != null, "Created device must not be null");
            Assert(dev1.DeviceId == "DEV-A", "DeviceId mismatch");
            Assert(dev1.DeviceName == "Headphones", "DeviceName mismatch");
            Assert(Math.Abs(dev1.MaxLimit - 0.30f) < 0.001f, "Device MaxLimit default mismatch");
            Assert(config.Devices.Count == 1, "Config must have 1 device");

            // Fetching again should return existing instance
            var dev1Again = store.GetOrCreateDeviceConfig(config, "DEV-A", "Renamed Headphones");
            Assert(object.ReferenceEquals(dev1, dev1Again), "Subsequent retrieval must return identical instance");
            Assert(config.Devices.Count == 1, "Config must still have 1 device");
            Console.WriteLine("  ✓ Test_GetOrCreateDeviceConfig_AddsAndRetrieves");
        }

        private static void Test_SilentLaunchArgs_DetectedCorrectly()
        {
            Assert(!ConfigStore.ShouldStartSilent(null), "null args should not be silent");
            Assert(!ConfigStore.ShouldStartSilent(new string[0]), "empty args should not be silent");
            Assert(!ConfigStore.ShouldStartSilent(new string[] { "open", "normal" }), "normal args should not be silent");

            Assert(ConfigStore.ShouldStartSilent(new string[] { "--tray" }), "--tray should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "-tray" }), "-tray should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "/tray" }), "/tray should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "-t" }), "-t should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "--minimized" }), "--minimized should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "-m" }), "-m should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "--silent" }), "--silent should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "-s" }), "-s should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "--startup" }), "--startup should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "--autostart" }), "--autostart should start silent");
            Assert(ConfigStore.ShouldStartSilent(new string[] { "random", "--tray" }), "multi-arg with --tray should start silent");
            Console.WriteLine("  ✓ Test_SilentLaunchArgs_DetectedCorrectly");
        }
    }
}
