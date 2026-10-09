using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace EarGuard.Config
{
    public class ConfigStore
    {
        private readonly IStartupRegistrar _startupRegistrar;
        private readonly ILegacyStartupRegistration _legacyStartupRegistration;
        private readonly IApplicationInstaller _applicationInstaller;
        private readonly string _executablePath;

        // The path handed to the startup registrar. Resolved once, because resolving installs the
        // running build into the durable per-user location and that must not happen on a read path.
        private readonly object _startupSync = new object();
        private string _resolvedStartupExecutable;

        // Serialises settings writes. The UI saves on every slider movement while the audio engine
        // saves from its own worker thread during device scans, so without this the same file could
        // be written concurrently. Re-entrant because EnsureValid is called from within Save.
        private readonly object _saveSync = new object();

        private readonly object _commitSync = new object();
        internal object SyncRoot { get { return _saveSync; } }
        public bool SettingsSaved { get; private set; }
        public event EventHandler SaveStatusChanged;

        public void Update(EarGuardConfig config, Action change)
        {
            lock (_saveSync)
            {
                change();
                EnsureValid(config);
            }
        }

        public string ConfigFilePath { get; private set; }

        public ConfigStore()
            : this(null, null, null, null)
        {
        }

        internal ConfigStore(
            IStartupRegistrar startupRegistrar,
            ILegacyStartupRegistration legacyStartupRegistration,
            string executablePath)
            : this(startupRegistrar, legacyStartupRegistration, executablePath, null)
        {
        }

        internal ConfigStore(
            IStartupRegistrar startupRegistrar,
            ILegacyStartupRegistration legacyStartupRegistration,
            string executablePath,
            IApplicationInstaller applicationInstaller, string settingsPath = null)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string earGuardDir = Path.Combine(appData, "EarGuard");
            SettingsSaved = true;
            ConfigFilePath = settingsPath ?? Path.Combine(earGuardDir, "settings.json");
            _startupRegistrar = startupRegistrar ?? new TaskSchedulerStartupRegistrar();
            _legacyStartupRegistration = legacyStartupRegistration ?? new LegacyRunStartupRegistration();
            _applicationInstaller = applicationInstaller ?? new ApplicationInstaller();
            _executablePath = executablePath ?? GetCurrentExecutablePath();
        }

        /// <summary>
        /// The absolute path logon startup should launch. On first use this installs the running
        /// build into the durable per-user directory, so startup never depends on wherever the user
        /// happened to launch EarGuard from.
        /// </summary>
        internal string ResolveStartupExecutable()
        {
            lock (_startupSync)
            {
                if (_resolvedStartupExecutable == null)
                {
                    _resolvedStartupExecutable = _applicationInstaller.ResolveStartupExecutable(_executablePath);
                }
                return _resolvedStartupExecutable;
            }
        }


        private static string GetCurrentExecutablePath()
        {
            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
            if (entryAssembly != null && !string.IsNullOrEmpty(entryAssembly.Location))
            {
                return entryAssembly.Location;
            }

            return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        }

        public EarGuardConfig Load(string customPath = null)
        {
            string targetPath = customPath ?? ConfigFilePath;
            EarGuardConfig config;
            lock (_commitSync)
            {
                if (!TryRead(targetPath, out config) && !TryRead(targetPath + ".bak", out config))
                    config = EarGuardConfig.CreateDefault();
            }

            // Synchronize the preference with either the current task or the legacy Run registration.
            bool startupEnabled = _startupRegistrar.IsEnabled() || _legacyStartupRegistration.IsEnabled();
            if (startupEnabled && !config.LaunchOnStartup)
            {
                config.LaunchOnStartup = true;
            }

            EnsureValid(config);
            return config;
        }

        private static bool TryRead(string path, out EarGuardConfig config)
        {
            config = null;
            try
            {
                config = new JavaScriptSerializer().Deserialize<EarGuardConfig>(File.ReadAllText(path));
                return config != null;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
            catch (InvalidCastException) { return false; }
            catch (Exception ex)
            {
                // Framework type converters wrap malformed numeric values in Exception.
                if (ex.InnerException is FormatException || ex.InnerException is OverflowException
                    || ex.InnerException is InvalidCastException) return false;
                throw;
            }
        }

        public bool Save(EarGuardConfig config, string customPath = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            bool saved;
            // Take snapshots in commit order. Disk I/O never holds the shared config/engine lock.
            lock (_commitSync)
            {
                string json;
                lock (_saveSync)
                {
                    EnsureValid(config);
                    json = FormatJson(new JavaScriptSerializer().Serialize(config));
                }
                string targetPath = customPath ?? ConfigFilePath;
                try
                {
                    string dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    string temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(targetPath))
                    {
                        EarGuardConfig previous;
                        // Never rotate corrupt input over the last usable backup; preserve it separately.
                        string backup = TryRead(targetPath, out previous) ? targetPath + ".bak"
                            : targetPath + ".corrupt." + Guid.NewGuid().ToString("N");
                        File.Replace(temporary, targetPath, backup);
                    }
                    else File.Move(temporary, targetPath);
                    saved = true;
                }
                catch (IOException) { saved = false; }
                catch (UnauthorizedAccessException) { saved = false; }
                SettingsSaved = saved;
            }
            var handler = SaveStatusChanged;
            if (handler != null) handler(this, EventArgs.Empty);
            return saved;
        }

        public void EnsureValid(EarGuardConfig config)
        {
            if (config == null) return;

            // Re-entrant with Save and shared with device lookups/additions.
            lock (_saveSync)
            {
                // Keep ceilings within the user's 1%-100% range. NaN takes the safest floor;
                // signed infinities clamp through the ordinary range comparisons.
                if (float.IsNaN(config.GlobalMaxLimit)) config.GlobalMaxLimit = 0.01f;
                if (config.GlobalMaxLimit < 0.01f) config.GlobalMaxLimit = 0.01f;
                if (config.GlobalMaxLimit > 1.0f) config.GlobalMaxLimit = 1.0f;

                if (float.IsNaN(config.GlobalSafePlugInVol) || float.IsInfinity(config.GlobalSafePlugInVol)) config.GlobalSafePlugInVol = 0.0f;
                if (config.GlobalSafePlugInVol < 0.00f) config.GlobalSafePlugInVol = 0.00f;
                if (config.GlobalSafePlugInVol > config.GlobalMaxLimit) config.GlobalSafePlugInVol = config.GlobalMaxLimit;

                if (config.Devices == null)
                {
                    config.Devices = new List<DeviceConfig>();
                }

                // Drop invalid null entries so downstream consumers never dereference them.
                config.Devices.RemoveAll(d => d == null);

                foreach (var dev in config.Devices)
                {
                    if (float.IsNaN(dev.MaxLimit)) dev.MaxLimit = 0.01f;
                    if (dev.MaxLimit < 0.01f) dev.MaxLimit = 0.01f;
                    if (dev.MaxLimit > 1.0f) dev.MaxLimit = 1.0f;

                    if (float.IsNaN(dev.SafePlugInVol) || float.IsInfinity(dev.SafePlugInVol)) dev.SafePlugInVol = 0.0f;
                    if (dev.SafePlugInVol < 0.00f) dev.SafePlugInVol = 0.00f;
                    if (dev.SafePlugInVol > dev.MaxLimit) dev.SafePlugInVol = dev.MaxLimit;
                }
            }
        }

        public DeviceConfig GetOrCreateDeviceConfig(EarGuardConfig config, string deviceId, string deviceName)
        {
            if (config == null) throw new ArgumentNullException("config");

            lock (_saveSync)
            {
                if (config.Devices == null) config.Devices = new List<DeviceConfig>();

                var existing = config.Devices.Find(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(deviceName))
                    {
                        existing.DeviceName = deviceName;
                    }
                    return existing;
                }

                var newDevice = new DeviceConfig
                {
                    DeviceId = deviceId,
                    DeviceName = !string.IsNullOrEmpty(deviceName) ? deviceName : "Audio Device",
                    Enabled = true,
                    MaxLimit = config.GlobalMaxLimit,
                    SafePlugInVol = config.GlobalSafePlugInVol
                };

                config.Devices.Add(newDevice);
                return newDevice;
            }
        }

        public bool SetStartupEnabled(bool enable)
        {
            if (enable)
            {
                string startupExecutable = ResolveStartupExecutable();
                if (string.IsNullOrEmpty(startupExecutable)) return false;
                if (!_startupRegistrar.Enable(startupExecutable)) return false;
                return _legacyStartupRegistration.Remove();
            }

            if (!_startupRegistrar.Disable()) return false;
            return _legacyStartupRegistration.Remove();
        }

        /// <summary>
        /// Reconciles the configured preference, the legacy Run registration and the logon task.
        ///
        /// Also repairs a registration that points at a path that no longer resolves - the failure
        /// this exists to prevent: the task survived, still fired at every logon, and launched
        /// nothing because the executable it named had been cleaned up.
        /// </summary>
        public bool MigrateStartupRegistrationIfNeeded(EarGuardConfig config = null)
        {
            bool taskEnabled = _startupRegistrar.IsEnabled();
            bool legacyEnabled = _legacyStartupRegistration.IsEnabled();
            bool shouldEnable = taskEnabled || legacyEnabled || (config != null && config.LaunchOnStartup);

            if (!shouldEnable) return true;

            string startupExecutable = ResolveStartupExecutable();
            if (string.IsNullOrEmpty(startupExecutable)) return false;

            // Rewrites the task whenever it is missing, points at another path, or names a stale
            // location; a task already bound to the durable path is left completely alone.
            if (!_startupRegistrar.IsEnabledFor(startupExecutable))
            {
                if (!_startupRegistrar.Enable(startupExecutable)) return false;
            }

            if (!_legacyStartupRegistration.Remove()) return false;

            if (config != null && !config.LaunchOnStartup)
            {
                Update(config, () => config.LaunchOnStartup = true);
                Save(config);
            }

            return true;
        }
        public bool IsStartupEnabled()
        {
            return _startupRegistrar.IsEnabled() || _legacyStartupRegistration.IsEnabled();
        }


        public static bool ShouldStartSilent(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            foreach (var arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;
                string clean = arg.TrimStart('-', '/').ToLowerInvariant();
                if (clean == "tray" || clean == "t" ||
                    clean == "minimized" || clean == "m" ||
                    clean == "silent" || clean == "s" ||
                    clean == "startup" || clean == "autostart")
                {
                    return true;
                }
            }
            return false;
        }

        private static string FormatJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            var sb = new System.Text.StringBuilder();
            int indent = 0;
            bool inQuote = false;
            int backslashes = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '\"' && backslashes % 2 == 0)
                {
                    inQuote = !inQuote;
                    sb.Append(ch);
                }
                else if (inQuote)
                {
                    sb.Append(ch);
                }
                else
                {
                    switch (ch)
                    {
                        case '{':
                        case '[':
                            sb.Append(ch);
                            sb.AppendLine();
                            indent++;
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case '}':
                        case ']':
                            sb.AppendLine();
                            indent--;
                            sb.Append(new string(' ', Math.Max(0, indent * 2)));
                            sb.Append(ch);
                            break;
                        case ',':
                            sb.Append(ch);
                            sb.AppendLine();
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case ':':
                            sb.Append(": ");
                            break;
                        default:
                            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
                            break;
                    }
                }
                backslashes = ch == '\\' ? backslashes + 1 : 0;
            }
            return sb.ToString();
        }
    }
}
