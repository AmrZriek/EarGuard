using System;

namespace EarGuard.Config
{
    public class DeviceConfig
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public bool Enabled { get; set; }
        public float MaxLimit { get; set; }
        public float SafePlugInVol { get; set; }

        public DeviceConfig()
        {
            DeviceId = string.Empty;
            DeviceName = string.Empty;
            Enabled = true;
            MaxLimit = 0.30f;
            SafePlugInVol = 0.05f;
        }

        public DeviceConfig Clone()
        {
            return new DeviceConfig
            {
                DeviceId = this.DeviceId,
                DeviceName = this.DeviceName,
                Enabled = this.Enabled,
                MaxLimit = this.MaxLimit,
                SafePlugInVol = this.SafePlugInVol
            };
        }
    }
}
