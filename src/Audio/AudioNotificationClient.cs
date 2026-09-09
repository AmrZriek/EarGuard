using System;
using System.Runtime.InteropServices;

namespace EarGuard.Audio
{
    public class AudioNotificationClient : IMMNotificationClient
    {
        private readonly Action _onDevicesChanged;

        public AudioNotificationClient(Action onDevicesChanged)
        {
            if (onDevicesChanged == null) throw new ArgumentNullException("onDevicesChanged");
            _onDevicesChanged = onDevicesChanged;
        }

        public int OnDeviceStateChanged(string pwstrDeviceId, int dwNewState)
        {
            TriggerNotification();
            return 0;
        }

        public int OnDeviceAdded(string pwstrDeviceId)
        {
            TriggerNotification();
            return 0;
        }

        public int OnDeviceRemoved(string pwstrDeviceId)
        {
            TriggerNotification();
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string pwstrDefaultDeviceId)
        {
            TriggerNotification();
            return 0;
        }

        public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key)
        {
            return 0;
        }

        private void TriggerNotification()
        {
            try
            {
                _onDevicesChanged();
            }
            catch (Exception)
            {
                // Isolate COM callback
            }
        }
    }
}
