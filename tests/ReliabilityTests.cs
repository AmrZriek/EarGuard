using System;
using System.IO;
using EarGuard.Config;

namespace EarGuard.Tests
{
    internal static class ReliabilityTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        internal static void RunAll()
        {
            var store = TestEnvironment.Store();
            var config = EarGuardConfig.CreateDefault();
            string[] names = { "quote \" comma, braces{}", "trailing\\", "two\\\\", "escaped\\\"", "[],:{}\\\\\\" };
            foreach (string name in names)
            {
                store.Update(config, () => config.Devices.Clear());
                store.GetOrCreateDeviceConfig(config, "test", name);
                Assert(store.Save(config), "Escaped-name save must succeed");
                Assert(store.Load().Devices[0].DeviceName == name, "JSON formatter must preserve quote/backslash parity");
            }
            string primary = File.ReadAllText(store.ConfigFilePath);
            store.Update(config, () => config.GlobalMaxLimit = 0.77f);
            using (var locked = new FileStream(store.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert(!store.Save(config) && !store.SettingsSaved, "Locked replacement must return failure");
                Assert(File.ReadAllText(store.ConfigFilePath) == primary, "Failed replacement must preserve primary bytes");
                Assert(config.GlobalMaxLimit == 0.77f, "Failed save must retain active settings");
            }
            Assert(store.Save(config) && store.SettingsSaved, "Saving must recover after the lock is released");
            string backup = File.ReadAllText(store.ConfigFilePath + ".bak");
            File.WriteAllText(store.ConfigFilePath, "{bad JSON");
            var recovered = store.Load();
            Assert(recovered.Devices[0].DeviceName == names[names.Length - 1], "Validated backup must precede defaults");
            Assert(store.Save(recovered), "Recovered configuration must be saveable");
            Assert(File.ReadAllText(store.ConfigFilePath + ".bak") == backup, "Corrupt primary must not replace good backup");
            Assert(Directory.GetFiles(Path.GetDirectoryName(store.ConfigFilePath), "*.corrupt.*").Length == 1,
                "Corrupt input must be preserved for inspection");
            string blocked = Path.Combine(TestEnvironment.ScratchDirectory(), "not-a-directory");
            foreach (string malformed in new[] { "{\"GlobalMaxLimit\":\"garbage\"}", "{\"GlobalMaxLimit\":1e100}",
                "{\"Devices\":[{\"Enabled\":\"garbage\"}]}" })
            {
                File.WriteAllText(store.ConfigFilePath, malformed);
                recovered = store.Load();
                Assert(recovered.Devices.Count > 0, "Malformed values must recover from the valid backup");
                Assert(store.Save(recovered), "Saving over malformed values must succeed");
                Assert(File.ReadAllText(store.ConfigFilePath + ".bak") == backup, "Malformed values must preserve backup bytes");
                var noBackup = TestEnvironment.Store();
                File.WriteAllText(noBackup.ConfigFilePath, malformed);
                Assert(noBackup.Load().GlobalMaxLimit == EarGuardConfig.CreateDefault().GlobalMaxLimit,
                    "Malformed values without a backup must recover defaults");
            }
            File.WriteAllText(blocked, "blocked");
            Assert(!store.Save(config, Path.Combine(blocked, "settings.json")), "Unwritable destination must return failure");

            string root = TestEnvironment.ScratchDirectory();
            string source = Path.Combine(root, "source.exe");
            string install = Path.Combine(root, "installed");
            Directory.CreateDirectory(install);
            var installer = new ApplicationInstaller(install);
            File.WriteAllText(source, "new executable");
            File.WriteAllText(installer.InstalledExecutablePath, "old executable");
            using (var locked = new FileStream(installer.InstalledExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert(installer.ResolveStartupExecutable(source) == installer.InstalledExecutablePath,
                    "Failed update should retain the durable executable");
                Assert(File.ReadAllText(installer.InstalledExecutablePath) == "old executable", "Failed update must preserve binary bytes");
            }
            installer.ResolveStartupExecutable(source);
            DateTime written = File.GetLastWriteTimeUtc(installer.InstalledExecutablePath);
            installer.ResolveStartupExecutable(source);
            Assert(File.GetLastWriteTimeUtc(installer.InstalledExecutablePath) == written, "Unchanged install must avoid replacement");
            Console.WriteLine("[PASS] Persistence recovery, escaping, locked writes and atomic installer regressions");
        }
    }
}
