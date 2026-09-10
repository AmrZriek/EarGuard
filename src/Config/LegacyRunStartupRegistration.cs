using Microsoft.Win32;

namespace EarGuard.Config
{
    internal sealed class LegacyRunStartupRegistration : ILegacyStartupRegistration
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryValueName = "EarGuard";

        public bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false))
                {
                    return key != null && key.GetValue(AppRegistryValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public bool Remove()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true))
                {
                    if (key == null) return true;
                    key.DeleteValue(AppRegistryValueName, false);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
