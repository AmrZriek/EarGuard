using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using EarGuard.Audio;
using EarGuard.Config;
using EarGuard.Tray;
using EarGuard.UI;

namespace EarGuard.Tests
{
    internal static class MainWindowTests
    {
        internal static void RunAll()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Test_HistoricalClampDoesNotOverwriteLiveVolume();
                    Test_DefaultDimensions_DoNotForceVerticalScrollbar();
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(10000)) throw new Exception("Hidden window regression timed out");
            if (failure != null) throw new Exception("Hidden window regression failed", failure);
            Console.WriteLine("[PASS] Real window keeps current volume after a historical clamp event");
            Console.WriteLine("[PASS] Default window dimensions do not force a vertical scrollbar on launch");
        }

        private static void Test_HistoricalClampDoesNotOverwriteLiveVolume()
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var store = TestEnvironment.Store();
            var config = EarGuardConfig.CreateDefault();
            config.LaunchOnStartup = false;
            config.ShowNotificationOnBlock = false;
            var endpoint = new AudioEngineTests.FakeEndpointVolume(0.80f);
            var device = new GuardedDevice
            {
                DeviceId = "window-test", VolumeControl = endpoint,
                Config = store.GetOrCreateDeviceConfig(config, "window-test", "Test endpoint")
            };
            // Shell objects have no handles: this test neither creates a tray icon nor signals the running app.
            using (var tray = (TrayManager)FormatterServices.GetUninitializedObject(typeof(TrayManager)))
            using (var instance = (SingleInstance)FormatterServices.GetUninitializedObject(typeof(SingleInstance)))
            using (var engine = TestEnvironment.Engine(store, config))
            {
                var devices = (List<GuardedDevice>)typeof(AudioEngine).GetField("_guardedDevices", fields).GetValue(engine);
                devices.Add(device);
                var window = new MainWindow(store, engine, tray, instance); // never shown
                try
                {
                    var gauge = (TextBlock)typeof(MainWindow).GetField("_liveVolText", fields).GetValue(window);
                    var status = (TextBlock)typeof(MainWindow).GetField("_protectionStatusText", fields).GetValue(window);
                    typeof(AudioEngine).GetMethod("WatchdogTick", fields).Invoke(engine, new object[] { null });
                    DrainDispatcher();
                    endpoint.Volume = 0.10f;
                    engine.ApplyVolumeCeiling(device, device.Config.MaxLimit);
                    DrainDispatcher();
                    if (gauge.Text != "Live: 10%") throw new Exception("Fixture must first display the newer volume");

                    // Deliver an old clamp after the current observation, through the actual UI subscription.
                    var clamped = (EventHandler<VolumeClampedEventArgs>)typeof(AudioEngine).GetField("VolumeClamped", fields).GetValue(engine);
                    clamped(engine, new VolumeClampedEventArgs(device, 0.80f, 0.30f));
                    DrainDispatcher();
                    if (gauge.Text != "Live: 10%") throw new Exception("Historical clamp overwrote the real live gauge: " + gauge.Text);
                    if (status.Text != "Last lowered: 80% → 30%.") throw new Exception("Clamp history must retain its original values");
                    if (endpoint.Volume != 0.10f || endpoint.WriteCount != 1)
                        throw new Exception("Rendering a historical event must not write hardware");
                }
                finally
                {
                    typeof(MainWindow).GetField("_reallyExit", fields).SetValue(window, true);
                    window.Close();
                }
            }
        }

        private static void Test_DefaultDimensions_DoNotForceVerticalScrollbar()
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var store = TestEnvironment.Store();
            var config = EarGuardConfig.CreateDefault();
            config.LaunchOnStartup = false;
            config.ShowNotificationOnBlock = false;
            var endpoint = new AudioEngineTests.FakeEndpointVolume(0.20f);
            var device = new GuardedDevice
            {
                DeviceId = "window-layout-test",
                DeviceName = "Realtek High Definition Audio (Default Communication Device)",
                Description = "Lowers volume above your ceiling.",
                VolumeControl = endpoint,
                Config = store.GetOrCreateDeviceConfig(config, "window-layout-test", "Realtek High Definition Audio")
            };
            using (var tray = (TrayManager)FormatterServices.GetUninitializedObject(typeof(TrayManager)))
            using (var instance = (SingleInstance)FormatterServices.GetUninitializedObject(typeof(SingleInstance)))
            using (var engine = TestEnvironment.Engine(store, config))
            {
                var devices = (List<GuardedDevice>)typeof(AudioEngine).GetField("_guardedDevices", fields).GetValue(engine);
                devices.Add(device);
                var window = new MainWindow(store, engine, tray, instance);
                try
                {
                    DrainDispatcher();
                    if (window.Width != 500) throw new Exception("Expected default Width = 500, got: " + window.Width);
                    if (window.Height != 520) throw new Exception("Expected default Height = 520, got: " + window.Height);
                    if (window.MinWidth != 480) throw new Exception("Expected default MinWidth = 480, got: " + window.MinWidth);
                    if (window.MinHeight != 480) throw new Exception("Expected default MinHeight = 480, got: " + window.MinHeight);
                    if (window.SizeToContent != System.Windows.SizeToContent.Height)
                        throw new Exception("Expected SizeToContent = Height, got: " + window.SizeToContent);

                    var rootGrid = (Grid)window.Content;
                    double chromeHeight = System.Windows.SystemParameters.WindowCaptionHeight + (System.Windows.SystemParameters.ResizeFrameHorizontalBorderHeight * 2);
                    if (chromeHeight <= 0) chromeHeight = 40;
                    double clientHeight = window.Height - chromeHeight;
                    rootGrid.Measure(new System.Windows.Size(window.Width, clientHeight));
                    rootGrid.Arrange(new System.Windows.Rect(0, 0, window.Width, clientHeight));
                    rootGrid.UpdateLayout();

                    var deviceCard = (Border)typeof(MainWindow).GetField("_deviceCard", fields).GetValue(window);
                    var scrollViewer = (ScrollViewer)deviceCard.Child;

                    if (scrollViewer.ComputedVerticalScrollBarVisibility != System.Windows.Visibility.Collapsed)
                    {
                        throw new Exception(string.Format(
                            "ScrollViewer vertical scrollbar is visible on default launch. ExtentHeight={0}, ViewportHeight={1}",
                            scrollViewer.ExtentHeight, scrollViewer.ViewportHeight));
                    }
                }
                finally
                {
                    typeof(MainWindow).GetField("_reallyExit", fields).SetValue(window, true);
                    window.Close();
                }
            }
        }

        private static void DrainDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
