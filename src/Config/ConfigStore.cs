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
        private readonly string _executablePath;

        public string ConfigFilePath { get; private set; }

        public ConfigStore()
            : this(null, null, null)
        {
        }

        internal ConfigStore(
            IStartupRegistrar startupRegistrar,
            ILegacyStartupRegistration legacyStartupRegistration,
            string executablePath)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string earGuardDir = Path.Combine(appData, "EarGuard");
            ConfigFilePath = Path.Combine(earGuardDir, "settings.json");
            _startupRegistrar = startupRegistrar ?? new TaskSchedulerStartupRegistrar();
            _legacyStartupRegistration = legacyStartupRegistration ?? new LegacyRunStartupRegistration();
            _executablePath = executablePath ?? GetCurrentExecutablePath();
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
            if (!File.Exists(targetPath))
            {
                config = EarGuardConfig.CreateDefault();
            }
            else
            {
                try
                {
                    string json = File.ReadAllText(targetPath);
                    var serializer = new JavaScriptSerializer();
                    config = serializer.Deserialize<EarGuardConfig>(json) ?? EarGuardConfig.CreateDefault();
                }
                catch (Exception)
                {
                    config = EarGuardConfig.CreateDefault();
                }
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

        public void Save(EarGuardConfig config, string customPath = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            EnsureValid(config);

            string targetPath = customPath ?? ConfigFilePath;
            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(config);

            // Simple formatting for readability
            json = FormatJson(json);
            File.WriteAllText(targetPath, json);
        }

        public void EnsureValid(EarGuardConfig config)
        {
            if (config == null) return;

            // Clamp global limits. 30% is the hard safety ceiling; lower user limits remain valid.
            if (config.GlobalMaxLimit < 0.01f) config.GlobalMaxLimit = 0.01f;
            if (config.GlobalMaxLimit > 0.30f) config.GlobalMaxLimit = 0.30f;

            if (config.GlobalSafePlugInVol < 0.00f) config.GlobalSafePlugInVol = 0.00f;
            if (config.GlobalSafePlugInVol > config.GlobalMaxLimit) config.GlobalSafePlugInVol = config.GlobalMaxLimit;

            if (config.Devices == null)
            {
                config.Devices = new List<DeviceConfig>();
            }

            foreach (var dev in config.Devices)
            {
                if (dev.MaxLimit < 0.01f) dev.MaxLimit = 0.01f;
                if (dev.MaxLimit > 0.30f) dev.MaxLimit = 0.30f;

                if (dev.SafePlugInVol < 0.00f) dev.SafePlugInVol = 0.00f;
                if (dev.SafePlugInVol > dev.MaxLimit) dev.SafePlugInVol = dev.MaxLimit;
            }
        }

        public DeviceConfig GetOrCreateDeviceConfig(EarGuardConfig config, string deviceId, string deviceName)
        {
            if (config == null) throw new ArgumentNullException("config");
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

        public bool SetStartupEnabled(bool enable)
        {
            if (enable)
            {
                if (!_startupRegistrar.Enable(_executablePath)) return false;
                return _legacyStartupRegistration.Remove();
            }

            if (!_startupRegistrar.Disable()) return false;
            return _legacyStartupRegistration.Remove();
        }

        public bool MigrateStartupRegistrationIfNeeded(EarGuardConfig config = null)
        {
            bool taskEnabled = _startupRegistrar.IsEnabled();
            bool legacyEnabled = _legacyStartupRegistration.IsEnabled();
            bool shouldEnable = taskEnabled || legacyEnabled || (config != null && config.LaunchOnStartup);

            if (!shouldEnable) return true;
            if (!_startupRegistrar.Enable(_executablePath)) return false;
            if (!_legacyStartupRegistration.Remove()) return false;

            if (config != null)
            {
                config.LaunchOnStartup = true;
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

            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '\"' && (i == 0 || json[i - 1] != '\\'))
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
            }
            return sb.ToString();
        }
    }
}
