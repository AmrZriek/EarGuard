using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using EarGuard.Config;

namespace EarGuard.Audio
{
    public class GuardedDevice : INotifyPropertyChanged, IDisposable
    {
        private float _currentVolume;
        private bool _isClampedAlert;

        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string Description { get; set; }

        public IMMDevice DeviceCom { get; set; }
        public IAudioEndpointVolume VolumeControl { get; set; }
        public AudioEndpointCallback CallbackInstance { get; set; }

        public DeviceConfig Config { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(DeviceName)) return DeviceName;
                if (!string.IsNullOrEmpty(Description)) return Description;
                return "Audio Endpoint";
            }
        }

        public float CurrentVolume
        {
            get { return _currentVolume; }
            set
            {
                if (Math.Abs(_currentVolume - value) > 0.001f)
                {
                    _currentVolume = value;
                    OnPropertyChanged("CurrentVolume");
                    OnPropertyChanged("CurrentVolumePercent");
                    OnPropertyChanged("CurrentVolumeText");
                }
            }
        }

        public int CurrentVolumePercent
        {
            get { return AudioEngine.ScalarToPercent(_currentVolume); }
        }

        public string CurrentVolumeText
        {
            get { return CurrentVolumePercent + "%"; }
        }


        public bool IsClampedAlert
        {
            get { return _isClampedAlert; }
            set
            {
                if (_isClampedAlert != value)
                {
                    _isClampedAlert = value;
                    OnPropertyChanged("IsClampedAlert");
                }
            }
        }


        public int MaxLimitPercent
        {
            get { return Config != null ? AudioEngine.ScalarToPercent(Config.MaxLimit) : 30; }
            set
            {
                if (Config != null)
                {
                    Config.MaxLimit = AudioEngine.PercentToScalar(value);
                    if (Config.SafePlugInVol > Config.MaxLimit)
                    {
                        Config.SafePlugInVol = Config.MaxLimit;
                        OnPropertyChanged("SafePlugInVolPercent");
                        OnPropertyChanged("SafePlugInVolText");
                    }
                    OnPropertyChanged("MaxLimitPercent");
                    OnPropertyChanged("MaxLimitText");
                }
            }
        }

        public string MaxLimitText
        {
            get { return MaxLimitPercent + "%"; }
        }

        public int SafePlugInVolPercent
        {
            get { return Config != null ? AudioEngine.ScalarToPercent(Config.SafePlugInVol) : 5; }
            set
            {
                if (Config != null)
                {
                    float scalar = AudioEngine.PercentToScalar(value);
                    if (scalar > Config.MaxLimit) scalar = Config.MaxLimit;
                    Config.SafePlugInVol = scalar;
                    OnPropertyChanged("SafePlugInVolPercent");
                    OnPropertyChanged("SafePlugInVolText");
                }
            }
        }

        public string SafePlugInVolText
        {
            get { return SafePlugInVolPercent + "%"; }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public void NotifyAllPropertiesChanged()
        {
            OnPropertyChanged("DisplayName");
            OnPropertyChanged("CurrentVolume");
            OnPropertyChanged("CurrentVolumePercent");
            OnPropertyChanged("CurrentVolumeText");
            OnPropertyChanged("MaxLimitPercent");
            OnPropertyChanged("MaxLimitText");
            OnPropertyChanged("SafePlugInVolPercent");
            OnPropertyChanged("SafePlugInVolText");
        }

        public void Dispose()
        {
            try
            {
                if (VolumeControl != null && CallbackInstance != null)
                {
                    VolumeControl.UnregisterControlChangeNotify(CallbackInstance);
                }
            }
            catch { }

            try
            {
                if (VolumeControl != null)
                {
                    Marshal.ReleaseComObject(VolumeControl);
                    VolumeControl = null;
                }
            }
            catch { }

            try
            {
                if (DeviceCom != null)
                {
                    Marshal.ReleaseComObject(DeviceCom);
                    DeviceCom = null;
                }
            }
            catch { }

            CallbackInstance = null;
        }
    }
}
