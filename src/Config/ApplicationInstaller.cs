using System;
using System.IO;
using System.Security.Cryptography;

namespace EarGuard.Config
{
    /// <summary>
    /// Owns the durable per-user copy of the executable that logon startup launches:
    /// <c>%LOCALAPPDATA%\EarGuard\EarGuard.exe</c>.
    ///
    /// EarGuard ships as a single portable executable, so the file a user first runs may live
    /// anywhere - a Downloads folder, a USB stick, a build tree. Registering startup against that
    /// path couples boot startup to a file the user is likely to move or delete, which breaks
    /// startup silently: the task still fires at logon and fails with "file not found" while the
    /// tray simply never appears. Copying the build into a stable per-user directory first means
    /// startup keeps working no matter what happens to the file EarGuard was launched from.
    ///
    /// No elevation is required: <c>%LOCALAPPDATA%</c> is always writable by the current user, and
    /// settings already live beside it in <c>%APPDATA%\EarGuard</c>, so moving the executable does
    /// not move the user's configuration.
    /// </summary>
    internal sealed class ApplicationInstaller : IApplicationInstaller
    {
        private const string DirectoryName = "EarGuard";
        private const string ExecutableFileName = "EarGuard.exe";

        private readonly string _installDirectory;

        public ApplicationInstaller()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DirectoryName))
        {
        }

        internal ApplicationInstaller(string installDirectory)
        {
            if (string.IsNullOrEmpty(installDirectory)) throw new ArgumentNullException("installDirectory");
            _installDirectory = installDirectory;
        }

        public string InstallDirectory
        {
            get { return _installDirectory; }
        }

        public string InstalledExecutablePath
        {
            get { return Path.Combine(_installDirectory, ExecutableFileName); }
        }

        public string ResolveStartupExecutable(string runningExecutablePath)
        {
            string runningPath = Normalize(runningExecutablePath);
            if (runningPath == null) return null;

            // Already the durable copy: nothing to install, and copying a running executable onto
            // itself would fail for no benefit.
            if (IsInstalledPath(runningPath)) return runningPath;

            string targetPath = InstalledExecutablePath;
            try
            {
                if (!Directory.Exists(_installDirectory))
                {
                    Directory.CreateDirectory(_installDirectory);
                }

                // Only rewrite when the installed copy is missing or from a different build, so a
                // normal logon does not touch the disk at all.
                if (!IsSameFile(runningPath, targetPath))
                {
                    File.Copy(runningPath, targetPath, true);
                }

                return targetPath;
            }
            catch
            {
                // The durable copy could not be written - a locked target, a read-only location, a
                // restricted profile. Prefer an existing durable copy over the launch location:
                // re-registering the volatile path would undo the very protection this provides and
                // silently restore the "task fires, tray never appears" failure once that path is
                // cleaned up. Only fall back when no durable copy exists at all.
                return File.Exists(InstalledExecutablePath) ? InstalledExecutablePath : runningPath;
            }
        }

        private bool IsInstalledPath(string absolutePath)
        {
            return string.Equals(absolutePath, InstalledExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSameFile(string firstPath, string secondPath)
        {
            var target = new FileInfo(secondPath);
            if (!target.Exists) return false;

            var source = new FileInfo(firstPath);
            if (!source.Exists) return false;
            if (source.Length != target.Length) return false;

            return string.Equals(HashFile(firstPath), HashFile(secondPath), StringComparison.OrdinalIgnoreCase);
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream));
            }
        }
    }
}
