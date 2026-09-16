using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
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
            Test_Validation_HonorsUserChosenCeilingUpTo100Percent();
            Test_Validation_NormalizesNonFiniteCeilings();
            Test_Validation_RemovesNullDeviceEntries();
            Test_ConcurrentSaveAndDeviceAddition_RoundTrips();
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
                GlobalMaxLimit = 1.50f, // above the legal 100% maximum
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

            Assert(config.GlobalMaxLimit <= 1.0f, "GlobalMaxLimit must be clamped <= 100%");
            Assert(config.GlobalSafePlugInVol >= 0.0f, "GlobalSafePlugInVol must be clamped >= 0.0f");
            Assert(config.Devices[0].MaxLimit >= 0.01f, "Device MaxLimit must be clamped >= 0.01");
            Assert(config.Devices[0].MaxLimit <= 1.0f, "Device MaxLimit must be clamped <= 100%");
            Assert(config.Devices[0].SafePlugInVol <= config.Devices[0].MaxLimit, "SafePlugInVol cannot exceed MaxLimit");
            Console.WriteLine("  ✓ Test_Validation_ClampsOutOfRangeValues");
        }

        private static void Test_Validation_HonorsUserChosenCeilingUpTo100Percent()
        {
            // Regression guard: validation used to silently snap every ceiling down to 30%,
            // so the UI showed one value while the engine enforced another.
            var store = new ConfigStore();
            var config = EarGuardConfig.CreateDefault();
            config.GlobalMaxLimit = 0.75f;
            config.Devices.Add(new DeviceConfig
            {
                DeviceId = "DEV-75",
                DeviceName = "Loud but intentional",
                MaxLimit = 0.75f,
                SafePlugInVol = 0.05f
            });

            store.EnsureValid(config);

            Assert(Math.Abs(config.GlobalMaxLimit - 0.75f) < 0.0001f,
                "A 75% global ceiling must be preserved exactly");
            Assert(Math.Abs(config.Devices[0].MaxLimit - 0.75f) < 0.0001f,
                "A 75% per-device ceiling must be preserved exactly");

            string tempFile = Path.Combine(Path.GetTempPath(), "EarGuard_Test_Ceiling_" + Guid.NewGuid() + ".json");
            try
            {
                store.Save(config, tempFile);
                var loaded = store.Load(tempFile);
                Assert(Math.Abs(loaded.Devices[0].MaxLimit - 0.75f) < 0.0001f,
                    "A saved 75% ceiling must survive a save/load round trip unchanged");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }

            Console.WriteLine("  ✓ Test_Validation_HonorsUserChosenCeilingUpTo100Percent");
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

        private static void Test_Validation_NormalizesNonFiniteCeilings()
        {
            var store = new ConfigStore();
            float[] values = { float.NegativeInfinity, float.NaN, float.PositiveInfinity };
            float[] expected = { 0.01f, 0.01f, 1.0f };
            for (int i = 0; i < values.Length; i++)
            {
                var config = EarGuardConfig.CreateDefault();
                config.GlobalMaxLimit = values[i];
                config.GlobalSafePlugInVol = 0.5f;
                config.Devices.Add(new DeviceConfig
                {
                    DeviceId = "NONFINITE",
                    MaxLimit = values[i],
                    SafePlugInVol = 0.5f
                });

                store.EnsureValid(config);

                Assert(config.GlobalMaxLimit == expected[i],
                    "Global non-finite ceiling must clamp safely: " + values[i]);
                Assert(config.Devices[0].MaxLimit == expected[i],
                    "Device non-finite ceiling must clamp safely: " + values[i]);
                Assert(config.GlobalSafePlugInVol == Math.Min(0.5f, expected[i]),
                    "Global plug-in volume must obey the normalized ceiling");
                Assert(config.Devices[0].SafePlugInVol == Math.Min(0.5f, expected[i]),
                    "Device plug-in volume must obey the normalized ceiling");
            }
        }

        private static void Test_Validation_RemovesNullDeviceEntries()
        {
            var store = new ConfigStore();
            string tempFile = Path.Combine(Path.GetTempPath(), "EarGuard_Test_Nulls_" + Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(tempFile,
                    "{\"Devices\":[null,{\"DeviceId\":\"KEEP\",\"DeviceName\":\"Desk DAC\",\"MaxLimit\":0.5},null]}");
                var config = store.Load(tempFile);
                Assert(config.Devices.Count == 1 && config.Devices[0].DeviceId == "KEEP",
                    "Loading must remove null entries while preserving valid devices");
                var existing = store.GetOrCreateDeviceConfig(config, "keep", "Renamed DAC");
                Assert(existing.DeviceName == "Renamed DAC" && config.Devices.Count == 1,
                    "Device lookup must find the surviving device case-insensitively");
                store.GetOrCreateDeviceConfig(config, "NEW", "New DAC");
                store.Save(config, tempFile);
                var loaded = store.Load(tempFile);
                Assert(loaded.Devices.Count == 2 && loaded.Devices.TrueForAll(d => d != null),
                    "Normalized devices and a subsequent addition must survive save/load");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        private static void Test_ConcurrentSaveAndDeviceAddition_RoundTrips()
        {
            var store = new ConfigStore();
            var config = EarGuardConfig.CreateDefault();
            config.GlobalMaxLimit = 0.75f;
            string tempFile = Path.Combine(Path.GetTempPath(), "EarGuard_Test_Concurrent_" + Guid.NewGuid() + ".json");
            const int deviceCount = 64;
            var errors = new Exception[3];
            var workers = new Thread[3];
            using (var start = new ManualResetEvent(false))
            {
                try
                {
                    for (int worker = 0; worker < workers.Length; worker++)
                    {
                        int index = worker;
                        workers[index] = new Thread(() =>
                        {
                            try
                            {
                                start.WaitOne();
                                for (int i = 0; i < deviceCount; i++)
                                {
                                    // Two scanners discover the same IDs while a third worker
                                    // saves. No caller field writes are made during the race.
                                    if (index != 0)
                                        store.GetOrCreateDeviceConfig(config, "DEV-" + i, "Device " + i);
                                    store.Save(config, tempFile);
                                }
                            }
                            catch (Exception ex)
                            {
                                errors[index] = ex;
                            }
                        });
                        workers[index].Start();
                    }
                    start.Set();
                    foreach (var worker in workers) worker.Join();
                    foreach (var error in errors)
                        if (error != null) throw new Exception("Concurrent store operation failed", error);

                    // Do not save again here: that could conceal an older in-flight save
                    // overwriting a newer device list after the scanners finish.
                    var loaded = store.Load(tempFile);
                    Assert(loaded.Devices.Count == deviceCount,
                        "Concurrent saves must persist every discovered device exactly once");
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var device in loaded.Devices)
                    {
                        Assert(ids.Add(device.DeviceId), "Concurrent discovery must not duplicate devices");
                        Assert(device.MaxLimit == 0.75f, "Discovered devices must retain the chosen ceiling");
                    }
                    for (int i = 0; i < deviceCount; i++)
                        Assert(ids.Contains("DEV-" + i), "Saved settings must retain discovered device " + i);
                }
                finally
                {
                    start.Set();
                    foreach (var worker in workers)
                        if (worker != null && worker.IsAlive) worker.Join();
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            }
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
