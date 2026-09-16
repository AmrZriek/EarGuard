using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EarGuard.Audio;
using EarGuard.Config;
using EarGuard.Tray;

namespace EarGuard.UI
{
    public class MainWindow : Window
    {
        private readonly ConfigStore _configStore;
        private readonly AudioEngine _engine;
        private readonly TrayManager _trayManager;
        private readonly SingleInstance _singleInstance;

        private ComboBox _deviceSelector;
        private Button _refreshBtn;

        private Border _deviceCard;
        private TextBlock _selectedDeviceTitle;
        private TextBlock _selectedDeviceDesc;
        private CheckBox _protectDeviceCheck;
        private Border _guardedStatusPill;
        private TextBlock _guardedStatusText;
        private TextBlock _ceilingBadge;
        private Slider _ceilingSlider;

        private TextBlock _safeVolBadge;
        private Slider _safeVolSlider;

        private TextBlock _liveVolText;
        private ProgressBar _liveVolBar;
        private TextBlock _protectionStatusText;

        private CheckBox _startupCheck;
        private CheckBox _notifyCheck;
        private Button _minimizeBtn;

        private GuardedDevice _currentSelectedDevice;
        private bool _isUpdatingUiFromCode = false;
        private bool _reallyExit = false;

        // True until the user picks a device themselves. While this is true the window follows
        // Windows' default playback endpoint, so plugging in a DAC (which becomes the new default)
        // moves the selection automatically. As soon as the user makes an explicit choice we stop
        // overriding them for the rest of the session.
        private bool _followDefaultDevice = true;

        public MainWindow(ConfigStore configStore, AudioEngine engine, TrayManager trayManager, SingleInstance singleInstance)
        {
            if (configStore == null) throw new ArgumentNullException("configStore");
            if (engine == null) throw new ArgumentNullException("engine");
            if (trayManager == null) throw new ArgumentNullException("trayManager");
            if (singleInstance == null) throw new ArgumentNullException("singleInstance");

            _configStore = configStore;
            _engine = engine;
            _trayManager = trayManager;
            _singleInstance = singleInstance;

            InitializeWindowSettings();
            BuildUi();
            BindEvents();
            RefreshDeviceDropdown();
        }

        private void InitializeWindowSettings()
        {
            Title = "EarGuard | Volume Spike Protector";
            Width = 500;
            Height = 540;
            MinWidth = 480;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)); // #F8FAFC
            FontFamily = new FontFamily("Segoe UI");

            try
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath));
                }
            }
            catch { }
        }

        private void BuildUi()
        {
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Device Selector
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Card
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer Settings

            // -----------------------------------------------------------------
            // 1. Header (Logo, Title, Active Pill)
            // -----------------------------------------------------------------
            var headerBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Logo
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Titles
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Active Pill

            // Logo Image (Seamlessly blended transparent vector render)
            var logoImage = new Image
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(logoImage, BitmapScalingMode.HighQuality);

            string logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard-transparent.png");
            if (!System.IO.File.Exists(logoPath))
            {
                logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard.png");
            }

            if (System.IO.File.Exists(logoPath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(logoPath);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    logoImage.Source = bmp;
                }
                catch { }
            }

            Grid.SetColumn(logoImage, 0);
            headerGrid.Children.Add(logoImage);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleText = new TextBlock
            {
                Text = "EarGuard",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            };
            var subtitleText = new TextBlock
            {
                Text = "Lowers unexpected endpoint volume spikes",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            };
            titleStack.Children.Add(titleText);
            titleStack.Children.Add(subtitleText);
            Grid.SetColumn(titleStack, 1);
            headerGrid.Children.Add(titleStack);

            // Active Pill
            var activePill = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(220, 252, 231)), // #DCFCE7
                BorderBrush = new SolidColorBrush(Color.FromRgb(134, 239, 172)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            var activeText = new TextBlock
            {
                Text = "● Active",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52))
            };
            activePill.Child = activeText;
            Grid.SetColumn(activePill, 2);
            headerGrid.Children.Add(activePill);

            headerBorder.Child = headerGrid;
            Grid.SetRow(headerBorder, 0);
            rootGrid.Children.Add(headerBorder);

            // -----------------------------------------------------------------
            // 2. Audio Device Selector Row
            // -----------------------------------------------------------------
            var selectorPanel = new Grid { Margin = new Thickness(16, 12, 16, 8) };
            selectorPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            selectorPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selectorPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var selectorLabel = new TextBlock
            {
                Text = "Audio Device:",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(selectorLabel, 0);
            selectorPanel.Children.Add(selectorLabel);

            _deviceSelector = new ComboBox
            {
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_deviceSelector, 1);
            selectorPanel.Children.Add(_deviceSelector);

            _refreshBtn = new Button
            {
                Content = "🔄",
                ToolTip = "Refresh audio endpoints",
                FontSize = 12,
                Width = 32,
                Height = 26,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Grid.SetColumn(_refreshBtn, 2);
            selectorPanel.Children.Add(_refreshBtn);

            Grid.SetRow(selectorPanel, 1);
            rootGrid.Children.Add(selectorPanel);

            // -----------------------------------------------------------------
            // 3. Main Protection Settings Card
            // -----------------------------------------------------------------
            _deviceCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(16, 4, 16, 10),
                Padding = new Thickness(16, 14, 16, 14)
            };

            var cardStack = new StackPanel();

            // Selected Device Title & Guard Badge
            var devTitleGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            devTitleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            devTitleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var devInfoStack = new StackPanel();
            _selectedDeviceTitle = new TextBlock
            {
                Text = "Select an audio device",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _selectedDeviceDesc = new TextBlock
            {
                Text = "Protection is active on this endpoint.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            devInfoStack.Children.Add(_selectedDeviceTitle);
            devInfoStack.Children.Add(_selectedDeviceDesc);
            Grid.SetColumn(devInfoStack, 0);
            devTitleGrid.Children.Add(devInfoStack);

            var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _protectDeviceCheck = new CheckBox
            {
                Content = "Protect",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            rightStack.Children.Add(_protectDeviceCheck);

            _guardedStatusPill = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            _guardedStatusText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Medium
            };
            _guardedStatusPill.Child = _guardedStatusText;
            rightStack.Children.Add(_guardedStatusPill);

            Grid.SetColumn(rightStack, 1);
            devTitleGrid.Children.Add(rightStack);
            cardStack.Children.Add(devTitleGrid);

            // Separator
            cardStack.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // 1. Hard Volume Ceiling Slider
            var ceilingHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            ceilingHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ceilingHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            ceilingHeader.Children.Add(new TextBlock
            {
                Text = "Hard Volume Ceiling",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
            });
            _ceilingBadge = new TextBlock
            {
                Text = "30%",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(2, 132, 199)) // Blue #0284C7
            };
            Grid.SetColumn(_ceilingBadge, 1);
            ceilingHeader.Children.Add(_ceilingBadge);
            cardStack.Children.Add(ceilingHeader);

            _ceilingSlider = new Slider
            {
                Minimum = 1,
                Maximum = 100,
                Value = 30,
                SmallChange = 1,
                LargeChange = 5,
                TickFrequency = 5,
                IsSnapToTickEnabled = false,
                Margin = new Thickness(0, 2, 0, 2)
            };
            cardStack.Children.Add(_ceilingSlider);

            cardStack.Children.Add(new TextBlock
            {
                Text = "Lowers volume above this limit after detection.",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // 2. Safe Plug-in / Wake Volume Slider
            var safeVolHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            safeVolHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            safeVolHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            safeVolHeader.Children.Add(new TextBlock
            {
                Text = "Safe Plug-in & Wake Limit",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
            });
            _safeVolBadge = new TextBlock
            {
                Text = "5%",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52)) // Green
            };
            Grid.SetColumn(_safeVolBadge, 1);
            safeVolHeader.Children.Add(_safeVolBadge);
            cardStack.Children.Add(safeVolHeader);

            _safeVolSlider = new Slider
            {
                Minimum = 0,
                Maximum = 30,
                Value = 5,
                SmallChange = 1,
                LargeChange = 5,
                Margin = new Thickness(0, 2, 0, 2)
            };
            cardStack.Children.Add(_safeVolSlider);

            cardStack.Children.Add(new TextBlock
            {
                Text = "Caps volume for 3 seconds on plug-in or wake. Does not deliberately raise quiet volume to a target.",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 0, 0, 14)
            });

            // 3. Live Volume Readout
            var liveVolGrid = new Grid();
            liveVolGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            liveVolGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var liveRow = new Grid();
            liveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            liveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _liveVolText = new TextBlock
            {
                Text = "Live: 0%",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Width = 65,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_liveVolText, 0);
            liveRow.Children.Add(_liveVolText);

            _liveVolBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 14,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246))
            };
            Grid.SetColumn(_liveVolBar, 1);
            liveRow.Children.Add(_liveVolBar);

            Grid.SetRow(liveRow, 0);
            liveVolGrid.Children.Add(liveRow);

            _protectionStatusText = new TextBlock
            {
                Text = "Select a device to view protection status.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(_protectionStatusText, 1);
            liveVolGrid.Children.Add(_protectionStatusText);

            cardStack.Children.Add(liveVolGrid);

            _deviceCard.Child = new ScrollViewer
            {
                Content = cardStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(_deviceCard, 2);
            rootGrid.Children.Add(_deviceCard);

            // -----------------------------------------------------------------
            // 4. Footer Settings (Startup, Notifications, Minimize)
            // -----------------------------------------------------------------
            var footerBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 10, 16, 12)
            };

            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var settingsStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            bool isStartup = _configStore.IsStartupEnabled();
            if (_engine.Config.LaunchOnStartup != isStartup)
            {
                _engine.Config.LaunchOnStartup = isStartup;
                _configStore.Save(_engine.Config);
            }

            _startupCheck = new CheckBox
            {
                Content = "Start with Windows",
                ToolTip = "Start EarGuard automatically in the system tray",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                IsChecked = isStartup,
                Margin = new Thickness(0, 0, 0, 4)
            };
            settingsStack.Children.Add(_startupCheck);

            _notifyCheck = new CheckBox
            {
                Content = "Notify when volume is lowered",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                IsChecked = _engine.Config.ShowNotificationOnBlock
            };
            settingsStack.Children.Add(_notifyCheck);

            Grid.SetColumn(settingsStack, 0);
            footerGrid.Children.Add(settingsStack);

            _minimizeBtn = new Button
            {
                Content = "Minimize to Tray",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Padding = new Thickness(12, 6, 12, 6),
                Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_minimizeBtn, 1);
            footerGrid.Children.Add(_minimizeBtn);

            footerBorder.Child = footerGrid;
            Grid.SetRow(footerBorder, 3);
            rootGrid.Children.Add(footerBorder);

            Content = rootGrid;
        }

        private void BindEvents()
        {
            _deviceSelector.SelectionChanged += (s, e) =>
            {
                var item = _deviceSelector.SelectedItem as ComboBoxItem;
                if (item == null) return;

                var dev = item.Tag as GuardedDevice;
                if (dev == null) return;

                // A selection change made by the user (not by our own refresh) means they have taken
                // manual control, so stop following the Windows default endpoint from now on.
                if (!_isUpdatingUiFromCode)
                {
                    _followDefaultDevice = false;
                }

                SelectDevice(dev);
            };

            // WPF does not raise SelectionChanged when the current item is committed again.
            // Only its commit keys count here; opening, Escape, and focus loss do not.
            _deviceSelector.PreviewKeyDown += (s, e) =>
            {
                if (_isUpdatingUiFromCode || !_deviceSelector.IsDropDownOpen || _deviceSelector.SelectedItem == null) return;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                if (key == Key.Enter || (key == Key.F4 && !alt) ||
                    (alt && (key == Key.Up || key == Key.Down)))
                {
                    _followDefaultDevice = false;
                }
            };

            _protectDeviceCheck.Checked += (s, e) =>
            {
                if (_isUpdatingUiFromCode || _currentSelectedDevice == null) return;
                _currentSelectedDevice.Config.Enabled = true;
                _configStore.Save(_engine.Config);
                UpdateDeviceProtectionUiState();

                // If the endpoint is louder than the ceiling, bring it down. This deliberately goes
                // through the engine so that every hardware volume write lives in one audited place.
                _engine.ApplyVolumeCeiling(_currentSelectedDevice, _currentSelectedDevice.Config.MaxLimit);
            };

            _protectDeviceCheck.Unchecked += (s, e) =>
            {
                if (_isUpdatingUiFromCode || _currentSelectedDevice == null) return;
                // Disabling does not request a volume change.
                _currentSelectedDevice.Config.Enabled = false;
                _configStore.Save(_engine.Config);
                UpdateDeviceProtectionUiState();
            };

            _refreshBtn.Click += (s, e) =>
            {
                _engine.ScanDevices();
                RefreshDeviceDropdown();
            };

            _ceilingSlider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUiFromCode || _currentSelectedDevice == null) return;

                int ceiling = (int)Math.Round(_ceilingSlider.Value);
                _ceilingBadge.Text = ceiling + "%";

                _currentSelectedDevice.Config.MaxLimit = AudioEngine.PercentToScalar(ceiling);

                // Adjust safe plug-in slider max
                _safeVolSlider.Maximum = ceiling;
                if (_safeVolSlider.Value > ceiling)
                {
                    _safeVolSlider.Value = ceiling;
                }

                // If the endpoint is louder than the new ceiling, bring it down through the engine.
                _engine.ApplyVolumeCeiling(_currentSelectedDevice, _currentSelectedDevice.Config.MaxLimit);

                _configStore.Save(_engine.Config);
            };

            _safeVolSlider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUiFromCode || _currentSelectedDevice == null) return;

                int safeVol = (int)Math.Round(_safeVolSlider.Value);
                _safeVolBadge.Text = safeVol + "%";

                _currentSelectedDevice.Config.SafePlugInVol = AudioEngine.PercentToScalar(safeVol);
                _configStore.Save(_engine.Config);
            };

            _startupCheck.Checked += (s, e) =>
            {
                _engine.Config.LaunchOnStartup = true;
                _configStore.SetStartupEnabled(true);
                _configStore.Save(_engine.Config);
            };

            _startupCheck.Unchecked += (s, e) =>
            {
                _engine.Config.LaunchOnStartup = false;
                _configStore.SetStartupEnabled(false);
                _configStore.Save(_engine.Config);
            };

            _notifyCheck.Checked += (s, e) =>
            {
                _engine.Config.ShowNotificationOnBlock = true;
                _configStore.Save(_engine.Config);
            };

            _notifyCheck.Unchecked += (s, e) =>
            {
                _engine.Config.ShowNotificationOnBlock = false;
                _configStore.Save(_engine.Config);
            };

            _minimizeBtn.Click += (s, e) => Hide();

            _engine.DevicesChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(RefreshDeviceDropdown));
            };

            _engine.VolumeChanged += (s, dev) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentSelectedDevice != null && string.Equals(_currentSelectedDevice.DeviceId, dev.DeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateLiveVolume(dev.CurrentVolume);
                    }
                }));
            };

            _engine.VolumeClamped += (s, args) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentSelectedDevice != null && _currentSelectedDevice.Config.Enabled &&
                        string.Equals(_currentSelectedDevice.DeviceId, args.Device.DeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateLiveVolume(args.ClampedVolume);
                        int oldPct = AudioEngine.ScalarToPercent(args.OldVolume);
                        int clampedPct = AudioEngine.ScalarToPercent(args.ClampedVolume);
                        _protectionStatusText.Text = string.Format("Last lowered: {0}% → {1}%.", oldPct, clampedPct);
                        _protectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                    }

                    if (_engine.Config.ShowNotificationOnBlock)
                    {
                        int oldVolPct = AudioEngine.ScalarToPercent(args.OldVolume);
                        int newVolPct = AudioEngine.ScalarToPercent(args.ClampedVolume);
                        _trayManager.ShowClampedNotification(args.Device.DisplayName, oldVolPct, newVolPct);
                    }
                }));
            };

            _trayManager.OpenRequested += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(RestoreAndActivate));
            };


            _trayManager.ExitRequested += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(ExitApplication));
            };
        }

        private void RefreshDeviceDropdown()
        {
            var devices = _engine.GuardedDevices;
            _trayManager.UpdateStatus(devices.Count);

            string previouslySelectedId = _currentSelectedDevice != null ? _currentSelectedDevice.DeviceId : null;

            // Until the user picks a device themselves, the window tracks Windows' default playback
            // endpoint. Without this, a device that was auto-selected once would stay selected
            // forever, even after the user swaps their default output by plugging in a DAC.
            string defaultId = _engine.DefaultDeviceId;
            bool isDefaultKnown = !string.IsNullOrEmpty(defaultId);

            string desiredId;
            if (_followDefaultDevice && isDefaultKnown)
            {
                desiredId = defaultId;
            }
            else
            {
                desiredId = previouslySelectedId;
            }

            _deviceSelector.Items.Clear();

            if (devices.Count == 0)
            {
                var emptyItem = new ComboBoxItem
                {
                    Content = "No active audio devices found",
                    IsEnabled = false
                };
                _deviceSelector.Items.Add(emptyItem);
                _deviceSelector.SelectedIndex = 0;
                _deviceCard.IsEnabled = false;
                _selectedDeviceTitle.Text = "No active audio devices found";
                _selectedDeviceDesc.Text = "Plug in your headphones or USB DAC dongle, then click Refresh.";
                return;
            }

            _deviceCard.IsEnabled = true;
            ComboBoxItem itemToSelect = null;

            foreach (var dev in devices)
            {
                var item = new ComboBoxItem
                {
                    Content = dev.DisplayName,
                    Tag = dev
                };
                item.PreviewMouseLeftButtonUp += (s, e) =>
                {
                    if (!_isUpdatingUiFromCode && _deviceSelector.IsDropDownOpen &&
                        ((ComboBoxItem)s).IsMouseOver)
                    {
                        _followDefaultDevice = false;
                    }
                };
                _deviceSelector.Items.Add(item);

                if (desiredId != null && string.Equals(dev.DeviceId, desiredId, StringComparison.OrdinalIgnoreCase))
                {
                    itemToSelect = item;
                }
            }

            _isUpdatingUiFromCode = true;
            try
            {
                if (itemToSelect != null)
                {
                    _deviceSelector.SelectedItem = itemToSelect;
                }
                else if (_deviceSelector.Items.Count > 0)
                {
                    _deviceSelector.SelectedIndex = 0;
                    itemToSelect = _deviceSelector.Items[0] as ComboBoxItem;
                }
            }
            finally
            {
                _isUpdatingUiFromCode = false;
            }

            if (itemToSelect != null)
            {
                SelectDevice(itemToSelect.Tag as GuardedDevice);
            }
        }

        private void SelectDevice(GuardedDevice dev)
        {
            _currentSelectedDevice = dev;
            if (dev == null) return;

            _isUpdatingUiFromCode = true;
            try
            {
                _selectedDeviceTitle.Text = dev.DisplayName;
                _selectedDeviceDesc.Text = !string.IsNullOrEmpty(dev.Description) ? dev.Description : "Hardware Audio Endpoint";

                int ceiling = AudioEngine.ScalarToPercent(dev.Config.MaxLimit);
                _ceilingBadge.Text = ceiling + "%";
                _ceilingSlider.Value = ceiling;

                int safeVol = AudioEngine.ScalarToPercent(dev.Config.SafePlugInVol);
                _safeVolBadge.Text = safeVol + "%";
                _safeVolSlider.Maximum = ceiling;
                _safeVolSlider.Value = safeVol;

                UpdateLiveVolume(dev.CurrentVolume);
                UpdateDeviceProtectionUiState();
            }
            finally
            {
                _isUpdatingUiFromCode = false;
            }
        }
        private void UpdateDeviceProtectionUiState()
        {
            if (_currentSelectedDevice == null || _currentSelectedDevice.Config == null) return;

            bool isGuarded = _currentSelectedDevice.Config.Enabled;
            _protectDeviceCheck.IsChecked = isGuarded;
            _protectionStatusText.Text = isGuarded
                ? "Brief spikes and hearing safety cannot be guaranteed."
                : "Protection is off. EarGuard is not limiting this device.";
            _protectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));

            if (isGuarded)
            {
                _guardedStatusPill.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231)); // #DCFCE7
                _guardedStatusPill.BorderBrush = new SolidColorBrush(Color.FromRgb(134, 239, 172)); // #86EFAC
                _guardedStatusPill.BorderThickness = new Thickness(1);
                _guardedStatusText.Text = "🛡️ Guarded";
                _guardedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));

                _ceilingSlider.IsEnabled = true;
                _safeVolSlider.IsEnabled = true;
                _ceilingSlider.Opacity = 1.0;
                _safeVolSlider.Opacity = 1.0;
                _selectedDeviceDesc.Text = "Lowers volume above your ceiling.";
            }
            else
            {
                _guardedStatusPill.Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)); // #F1F5F9
                _guardedStatusPill.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)); // #CBD5E1
                _guardedStatusPill.BorderThickness = new Thickness(1);
                _guardedStatusText.Text = "⚪ Unprotected";
                _guardedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));

                _ceilingSlider.IsEnabled = false;
                _safeVolSlider.IsEnabled = false;
                _ceilingSlider.Opacity = 0.55;
                _safeVolSlider.Opacity = 0.55;
                _selectedDeviceDesc.Text = "Not protected. This device's volume will not be limited.";
            }
        }


        private void UpdateLiveVolume(float volScalar)
        {
            int volPct = AudioEngine.ScalarToPercent(volScalar);
            _liveVolText.Text = string.Format("Live: {0}%", volPct);
            _liveVolBar.Value = volPct;

            if (_currentSelectedDevice != null && volPct > AudioEngine.ScalarToPercent(_currentSelectedDevice.Config.MaxLimit))
            {
                _liveVolBar.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red if spiking
            }
            else
            {
                _liveVolBar.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue normal
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Hook WndProc to listen for single-instance restore broadcast
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_singleInstance != null && (uint)msg == _singleInstance.RestoreMessageId)
            {
                RestoreAndActivate();
                handled = true;
            }
            else if (msg == CoreAudioConstants.WM_POWERBROADCAST)
            {
                int powerEvent = wParam.ToInt32();
                if (powerEvent == CoreAudioConstants.PBT_APMRESUMEAUTOMATIC ||
                    powerEvent == CoreAudioConstants.PBT_APMRESUMESUSPEND)
                {
                    _engine.HandleSystemResume();
                }
            }
            return IntPtr.Zero;
        }

        public void RestoreAndActivate()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_reallyExit)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                base.OnClosing(e);
            }
        }

        public void ExitApplication()
        {
            _reallyExit = true;
            Close();
            Application.Current.Shutdown();
        }
    }
}
