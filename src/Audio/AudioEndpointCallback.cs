using System;
using System.Runtime.InteropServices;

namespace EarGuard.Audio
{
    public class AudioEndpointCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action<AUDIO_VOLUME_NOTIFICATION_DATA> _onNotifyAction;

        public AudioEndpointCallback(Action<AUDIO_VOLUME_NOTIFICATION_DATA> onNotifyAction)
        {
            if (onNotifyAction == null) throw new ArgumentNullException("onNotifyAction");
            _onNotifyAction = onNotifyAction;
        }

        public int OnNotify(IntPtr pNotify)
        {
            if (pNotify == IntPtr.Zero) return 0;

            try
            {
                var data = (AUDIO_VOLUME_NOTIFICATION_DATA)Marshal.PtrToStructure(
                    pNotify,
                    typeof(AUDIO_VOLUME_NOTIFICATION_DATA)
                );
                _onNotifyAction(data);
            }
            catch (Exception)
            {
                // Never allow unhandled exceptions to escape COM callback boundary
            }

            return 0; // S_OK
        }
    }
}
