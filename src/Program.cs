using System;
using System.Windows;
using EarGuard.Audio;
using EarGuard.Config;
using EarGuard.Tray;
using EarGuard.UI;

namespace EarGuard
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var singleInstance = new SingleInstance();
            if (!singleInstance.TryAcquire())
            {
                // Another instance is already running; signal it to restore window and exit
                singleInstance.NotifyExistingInstance();
                return;
            }

            try
            {
                var configStore = new ConfigStore();
                var config = configStore.Load();
                configStore.MigrateStartupRegistryIfNeeded();

                var audioEngine = new AudioEngine(configStore, config);
                var trayManager = new TrayManager();

                audioEngine.Start();

                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var mainWindow = new MainWindow(configStore, audioEngine, trayManager, singleInstance);

                // Ensure Win32 HWND is created so single-instance restore broadcasts
                // are received even if EarGuard starts silently in the system tray
                new System.Windows.Interop.WindowInteropHelper(mainWindow).EnsureHandle();

                // Open silently in tray if launched via startup / tray flag
                if (!ConfigStore.ShouldStartSilent(args))
                {
                    mainWindow.Show();
                }

                app.Exit += (s, e) =>
                {
                    try { trayManager.Dispose(); } catch { }
                    try { audioEngine.Dispose(); } catch { }
                    try { singleInstance.Dispose(); } catch { }
                };

                app.Run();
            }
            finally
            {
                singleInstance.Dispose();
            }
        }

    }
}
