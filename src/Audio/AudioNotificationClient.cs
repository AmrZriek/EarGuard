using System;
using System.Runtime.InteropServices;

namespace EarGuard.Audio
{
    public class AudioNotificationClient : IMMNotificationClient
    {
        private readonly Action _onDevicesChanged;
        internal event Action<string> DeviceDisconnected;

        public AudioNotificationClient(Action onDevicesChanged)
        {
            if (onDevicesChanged == null) throw new ArgumentNullException("onDevicesChanged");
            _onDevicesChanged = onDevicesChanged;
        }

        public int OnDeviceStateChanged(string pwstrDeviceId, int dwNewState)
        {
            if (dwNewState != CoreAudioConstants.DEVICE_STATE_ACTIVE) NotifyDisconnected(pwstrDeviceId);
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
            NotifyDisconnected(pwstrDeviceId);
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

        private void NotifyDisconnected(string deviceId)
        {
            try
            {
                var handler = DeviceDisconnected;
                if (handler != null && !string.IsNullOrEmpty(deviceId)) handler(deviceId);
            }
            catch (Exception)
            {
                // Isolate COM callback.
            }
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
