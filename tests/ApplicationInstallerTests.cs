using System;
using System.IO;
using EarGuard.Config;

namespace EarGuard.Tests
{
    /// <summary>
    /// Covers the durable-install behavior that keeps logon startup working when the file EarGuard
    /// was launched from later disappears.
    /// </summary>
    public static class ApplicationInstallerTests
    {
        public static void RunAll()
        {
            Console.WriteLine("[TEST] Running ApplicationInstallerTests...");
            Test_ResolveInstallsIntoStableLocation();
            Test_ResolveIsIdempotentForInstalledCopy();
            Test_ResolveRefreshesWhenInstalledCopyIsStale();
            Test_ResolveFallsBackWhenInstallIsImpossible();
            Console.WriteLine("[PASS] All ApplicationInstallerTests passed!");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("Assertion Failed: " + message);
        }

        private static string NewScratchDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "EarGuard_Install_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Test_ResolveInstallsIntoStableLocation()
        {
            string root = NewScratchDirectory();
            try
            {
                string launchDir = Path.Combine(root, "Downloads");
                string installDir = Path.Combine(root, "LocalAppData", "EarGuard");
                Directory.CreateDirectory(launchDir);

                string launched = Path.Combine(launchDir, "EarGuard.exe");
                File.WriteAllText(launched, "build-one");

                var installer = new ApplicationInstaller(installDir);
                string resolved = installer.ResolveStartupExecutable(launched);

                Assert(resolved == installer.InstalledExecutablePath,
                    "Startup must be registered against the durable install path");
                Assert(File.Exists(resolved), "The durable copy must exist after resolving");
                Assert(!string.Equals(resolved, launched, StringComparison.OrdinalIgnoreCase),
                    "The durable copy must not be the launch location");

                // The whole point: deleting the original must not affect the registered target.
                File.Delete(launched);
                Assert(File.Exists(resolved),
                    "The registered executable must survive deletion of the original launch file");
                Assert(File.ReadAllText(resolved) == "build-one", "Installed copy must match the running build");
                Console.WriteLine("  ✓ Test_ResolveInstallsIntoStableLocation");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_ResolveIsIdempotentForInstalledCopy()
        {
            string root = NewScratchDirectory();
            try
            {
                var installer = new ApplicationInstaller(root);
                string installed = installer.InstalledExecutablePath;
                File.WriteAllText(installed, "already-installed");

                string resolved = installer.ResolveStartupExecutable(installed);

                Assert(resolved == installed, "An already-installed copy must resolve to itself");
                Assert(File.ReadAllText(installed) == "already-installed",
                    "Resolving an installed copy must not rewrite it");
                Console.WriteLine("  ✓ Test_ResolveIsIdempotentForInstalledCopy");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_ResolveRefreshesWhenInstalledCopyIsStale()
        {
            string root = NewScratchDirectory();
            try
            {
                string launchDir = Path.Combine(root, "src");
                string installDir = Path.Combine(root, "install");
                Directory.CreateDirectory(launchDir);
                Directory.CreateDirectory(installDir);

                string launched = Path.Combine(launchDir, "EarGuard.exe");
                File.WriteAllText(launched, "build-two");

                string installed = Path.Combine(installDir, "EarGuard.exe");
                File.WriteAllText(installed, "build-one-older");

                var installer = new ApplicationInstaller(installDir);
                string resolved = installer.ResolveStartupExecutable(launched);

                Assert(resolved == installer.InstalledExecutablePath, "Must resolve to the install path");
                Assert(File.ReadAllText(resolved) == "build-two",
                    "A newer build must replace a stale installed copy");
                Console.WriteLine("  ✓ Test_ResolveRefreshesWhenInstalledCopyIsStale");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_ResolveFallsBackWhenInstallIsImpossible()
        {
            string root = NewScratchDirectory();
            try
            {
                string launched = Path.Combine(root, "EarGuard.exe");
                File.WriteAllText(launched, "build-one");

                // A file where the install directory should be makes the copy impossible.
                string blocked = Path.Combine(root, "blocked");
                File.WriteAllText(blocked, "not a directory");

                var installer = new ApplicationInstaller(blocked);
                string resolved = installer.ResolveStartupExecutable(launched);

                Assert(resolved == launched,
                    "When installing is impossible, startup must still register a working executable");
                Console.WriteLine("  ✓ Test_ResolveFallsBackWhenInstallIsImpossible");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void TryDelete(string directory)
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
