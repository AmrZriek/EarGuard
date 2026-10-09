using System;
using System.IO;
using EarGuard.Config;

namespace EarGuard.Tests
{
    internal static class TestEnvironment
    {
        internal static readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "artifacts", Guid.NewGuid().ToString("N"));

        internal static string ScratchDirectory()
        {
            string path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static ConfigStore Store(IStartupRegistrar registrar = null,
            ILegacyStartupRegistration legacy = null, string executable = null,
            IApplicationInstaller installer = null)
        {
            return new ConfigStore(registrar ?? new Startup(), legacy ?? new Legacy(),
                executable ?? Path.Combine(Root, "EarGuard.exe"), installer ?? new Installer(),
                Path.Combine(ScratchDirectory(), "settings.json"));
        }

        internal static EarGuard.Audio.AudioEngine Engine(ConfigStore store, EarGuardConfig config)
        {
            return new EarGuard.Audio.AudioEngine(store, config, () => null);
        }

        private sealed class Startup : IStartupRegistrar
        {
            public bool IsEnabled() { return false; }
            public bool IsEnabledFor(string path) { return false; }
            public bool Enable(string path) { return true; }
            public bool Disable() { return true; }
        }
        private sealed class Legacy : ILegacyStartupRegistration
        {
            public bool IsEnabled() { return false; }
            public bool Remove() { return true; }
        }
        private sealed class Installer : IApplicationInstaller
        {
            public string ResolveStartupExecutable(string path) { return path; }
        }
    }
}
