using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace EarGuard.Config
{
    public class ConfigStore
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryValueName = "EarGuard";

        public string ConfigFilePath { get; private set; }

        public ConfigStore()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string earGuardDir = Path.Combine(appData, "EarGuard");
            ConfigFilePath = Path.Combine(earGuardDir, "settings.json");
        }

        public EarGuardConfig Load(string customPath = null)
        {
            string targetPath = customPath ?? ConfigFilePath;
            if (!File.Exists(targetPath))
            {
                var defaultConfig = EarGuardConfig.CreateDefault();
                EnsureValid(defaultConfig);
                return defaultConfig;
            }

            try
            {
                string json = File.ReadAllText(targetPath);
                var serializer = new JavaScriptSerializer();
                var config = serializer.Deserialize<EarGuardConfig>(json);
                if (config == null)
                {
                    config = EarGuardConfig.CreateDefault();
                }
                EnsureValid(config);
                return config;
            }
            catch (Exception)
            {
                var fallback = EarGuardConfig.CreateDefault();
                EnsureValid(fallback);
                return fallback;
            }
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

            // Clamp global limits
            if (config.GlobalMaxLimit < 0.01f) config.GlobalMaxLimit = 0.01f;
            if (config.GlobalMaxLimit > 1.00f) config.GlobalMaxLimit = 1.00f;

            if (config.GlobalSafePlugInVol < 0.00f) config.GlobalSafePlugInVol = 0.00f;
            if (config.GlobalSafePlugInVol > config.GlobalMaxLimit) config.GlobalSafePlugInVol = config.GlobalMaxLimit;

            if (config.Devices == null)
            {
                config.Devices = new List<DeviceConfig>();
            }

            foreach (var dev in config.Devices)
            {
                if (dev.MaxLimit < 0.01f) dev.MaxLimit = 0.01f;
                if (dev.MaxLimit > 1.00f) dev.MaxLimit = 1.00f;

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

        public bool SetStartupRegistry(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true))
                {
                    if (key == null) return false;

                    if (enable)
                    {
                        string exePath = System.Reflection.Assembly.GetEntryAssembly() != null
                            ? System.Reflection.Assembly.GetEntryAssembly().Location
                            : System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

                        key.SetValue(AppRegistryValueName, "\"" + exePath + "\" --tray");
                    }
                    else
                    {
                        if (key.GetValue(AppRegistryValueName) != null)
                        {
                            key.DeleteValue(AppRegistryValueName, false);
                        }
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public void MigrateStartupRegistryIfNeeded()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true))
                {
                    if (key == null) return;
                    object val = key.GetValue(AppRegistryValueName);
                    if (val != null)
                    {
                        string strVal = val.ToString();
                        if (!strVal.Contains("--tray") && !strVal.Contains("--minimized"))
                        {
                            string exePath = System.Reflection.Assembly.GetEntryAssembly() != null
                                ? System.Reflection.Assembly.GetEntryAssembly().Location
                                : System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

                            key.SetValue(AppRegistryValueName, "\"" + exePath + "\" --tray");
                        }
                    }
                }
            }
            catch { }
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

        public bool IsStartupRegistryEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false))
                {
                    if (key == null) return false;
                    return key.GetValue(AppRegistryValueName) != null;
                }
            }
            catch
            {
                return false;
            }
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
