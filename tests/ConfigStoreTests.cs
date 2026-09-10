using System;
using System.IO;
using Microsoft.Win32;
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
            Test_StartupRegistration_DelegatesToRegistrar();
            Test_StartupTaskDefinition_IsPortableAndDeterministic();
            Test_StartupMigration_PreservesLegacyOnTaskFailure();
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
                    GlobalMaxLimit = 0.22f,
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
                Assert(Math.Abs(loaded.GlobalMaxLimit - 0.22f) < 0.001f, "Loaded GlobalMaxLimit mismatch");
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
                GlobalMaxLimit = 1.50f, // above the 30% safety ceiling
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

            Assert(config.GlobalMaxLimit <= 0.30f, "GlobalMaxLimit must be clamped <= 30%");
            Assert(config.GlobalSafePlugInVol >= 0.0f, "GlobalSafePlugInVol must be clamped >= 0.0f");
            Assert(config.Devices[0].MaxLimit >= 0.01f, "Device MaxLimit must be clamped >= 0.01");
            Assert(config.Devices[0].MaxLimit <= 0.30f, "Device MaxLimit must be clamped <= 30%");
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
            Assert(dev1.DeviceName == "Headphones", "DeviceName mismatch");
            Assert(Math.Abs(dev1.MaxLimit - 0.30f) < 0.001f, "Device MaxLimit default mismatch");
            Assert(Math.Abs(dev1.SafePlugInVol - 0.05f) < 0.001f, "Device SafePlugInVol default mismatch");
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

        private static void Test_StartupRegistration_DelegatesToRegistrar()
        {
            var registrar = new FakeStartupRegistrar { Enabled = false };
            var legacy = new FakeLegacyStartupRegistration { Enabled = false };
            var store = new ConfigStore(registrar, legacy, @"D:\Portable Folder\EarGuard.exe");

            Assert(!store.IsStartupEnabled(), "Disabled task must report disabled");
            Assert(store.SetStartupEnabled(true), "Enable must succeed when registrar succeeds");
            Assert(registrar.EnableCalls == 1, "Enable must delegate exactly once");
            Assert(registrar.LastExecutablePath.EndsWith("EarGuard.exe", StringComparison.OrdinalIgnoreCase),
                "Enable must register the current executable");
            Assert(legacy.RemoveCalls == 1, "Enable must remove only the legacy EarGuard registration");

            Assert(store.SetStartupEnabled(false), "Disable must succeed when registrar succeeds");
            Assert(registrar.DisableCalls == 1, "Disable must delegate exactly once");
            Console.WriteLine("  ✓ Test_StartupRegistration_DelegatesToRegistrar");
        }

        private static void Test_StartupTaskDefinition_IsPortableAndDeterministic()
        {
            var definition = StartupTaskDefinition.Build(@"D:\Portable Folder\EarGuard.exe");
            Assert(definition.TaskName == "EarGuardStartup", "Task definition must use the EarGuardStartup task");
            Assert(definition.TriggerLogon, "Task definition must trigger at user logon");
            Assert(definition.Delay == TimeSpan.Zero, "Task definition must have zero configured delay");
            Assert(definition.InteractiveOnly, "Task definition must be interactive-only");
            Assert(definition.LimitedPrivilege, "Task definition must use limited privilege");
            Assert(definition.ExecutablePath.EndsWith(@"Portable Folder\EarGuard.exe"), "Task definition must contain the absolute executable path");
            Assert(definition.Arguments == "--tray", "Task definition must launch in the tray");
            Assert(definition.WorkingDirectory.EndsWith(@"Portable Folder"), "Task definition must launch from the executable directory");
            Console.WriteLine("  ✓ Test_StartupTaskDefinition_IsPortableAndDeterministic");
        }

        private static void Test_StartupMigration_PreservesLegacyOnTaskFailure()
        {
            var registrar = new FakeStartupRegistrar { EnableResult = false };
            var legacy = new FakeLegacyStartupRegistration { Enabled = true };
            var store = new ConfigStore(registrar, legacy, @"D:\Portable Folder\EarGuard.exe");
            var config = EarGuardConfig.CreateDefault();
            config.LaunchOnStartup = false;

            bool migrated = store.MigrateStartupRegistrationIfNeeded(config);

            Assert(!migrated, "Migration must report task creation failure");
            Assert(registrar.EnableCalls == 1, "Migration must attempt the task exactly once");
            Assert(legacy.RemoveCalls == 0, "Legacy registration must remain when task creation fails");
            Assert(legacy.Enabled, "Legacy registration must remain enabled on failure");
            Console.WriteLine("  ✓ Test_StartupMigration_PreservesLegacyOnTaskFailure");
        }

        private sealed class FakeStartupRegistrar : IStartupRegistrar
        {
            public bool Enabled;
            public bool EnableResult = true;
            public int EnableCalls;
            public int DisableCalls;
            public string LastExecutablePath;

            public bool IsEnabled() { return Enabled; }

            public bool Enable(string executablePath)
            {
                EnableCalls++;
                LastExecutablePath = executablePath;
                if (!EnableResult) return false;
                Enabled = true;
                return true;
            }

            public bool Disable()
            {
                DisableCalls++;
                Enabled = false;
                return true;
            }
        }

        private sealed class FakeLegacyStartupRegistration : ILegacyStartupRegistration
        {
            public bool Enabled;
            public int RemoveCalls;

            public bool IsEnabled() { return Enabled; }

            public bool Remove()
            {
                RemoveCalls++;
                Enabled = false;
                return true;
            }
        }
    }
}
