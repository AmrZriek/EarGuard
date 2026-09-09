using System;
using System.Collections.Generic;

namespace EarGuard.Config
{
    public class EarGuardConfig
    {
        public float GlobalMaxLimit { get; set; }
        public float GlobalSafePlugInVol { get; set; }
        public bool LaunchOnStartup { get; set; }
        public bool ShowNotificationOnBlock { get; set; }
        public List<DeviceConfig> Devices { get; set; }

        public EarGuardConfig()
        {
            GlobalMaxLimit = 0.30f;
            GlobalSafePlugInVol = 0.05f;
            LaunchOnStartup = false;
            ShowNotificationOnBlock = true;
            Devices = new List<DeviceConfig>();
        }

        public static EarGuardConfig CreateDefault()
        {
            return new EarGuardConfig();
        }
    }
}
