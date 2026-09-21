using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EarGuard.Tray
{
    public class TrayManager : IDisposable
    {
        // --- Silent notification-area balloon (Shell_NotifyIcon with NIIF_NOSOUND) ---
        //
        // The managed NotifyIcon.ShowBalloonTip API always plays the Windows notification sound.
        // For a hearing-protection tool that sound is itself a hazard: a spike can arrive while the
        // user is wearing IEMs at a quiet listening level, and the "protection" notice would then be
        // the loudest thing they hear. NIIF_NOSOUND suppresses it, and the only supported way to set
        // that flag is to call Shell_NotifyIcon directly.
        private const int NIM_MODIFY = 0x00000001;
        private const int NIF_INFO = 0x00000010;
        private const int NIIF_NONE = 0x00000000;
        private const int NIIF_NOSOUND = 0x00000010;
        private const int NIIF_RESPECT_QUIET_TIME = 0x00000080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _contextMenu;
        private ToolStripMenuItem _openMenuItem;
        private ToolStripMenuItem _exitMenuItem;

        private Icon _trayIcon;
        private DateTime _lastBalloonTime = DateTime.MinValue;
        private TaskbarListener _taskbarListener;
        private uint _taskbarCreatedMessage;
        public event EventHandler OpenRequested;
        public event EventHandler ExitRequested;

        public TrayManager()
        {
            InitializeIcon();
            InitializeContextMenu();
            InitializeNotifyIcon();
            InitializeTaskbarRecovery();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        private void InitializeTaskbarRecovery()
        {
            try
            {
                _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
                if (_taskbarCreatedMessage == 0) return;
                _taskbarListener = new TaskbarListener(_taskbarCreatedMessage);
                _taskbarListener.TaskbarCreated += OnTaskbarCreated;
            }
            catch { }
        }

        private void OnTaskbarCreated()
        {
            try
            {
                if (_notifyIcon == null) return;
                TrayIconRecovery.ReaddAfterTaskbarCreated(new NotifyIconAdapter(_notifyIcon));
            }
            catch { }
        }

        private sealed class NotifyIconAdapter : ITrayIconVisibility
        {
            private readonly NotifyIcon _icon;

            public NotifyIconAdapter(NotifyIcon icon)
            {
                _icon = icon;
            }

            public bool Visible
            {
                get { return _icon.Visible; }
                set { _icon.Visible = value; }
            }
        }

        private sealed class TaskbarListener : NativeWindow, IDisposable
        {
            private readonly uint _message;
            public event Action TaskbarCreated;

            public TaskbarListener(uint message)
            {
                _message = message;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == _message)
                {
                    try
                    {
                        if (TaskbarCreated != null) TaskbarCreated();
                    }
                    catch { }
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                try { DestroyHandle(); }
                catch { }
            }
        }

        private void InitializeIcon()
        {
            string pngPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard-transparent.png");
            if (!System.IO.File.Exists(pngPath))
            {
                pngPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard.png");
            }

            if (System.IO.File.Exists(pngPath))
            {
                try
                {
                    using (var stream = new System.IO.FileStream(pngPath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                    using (var bmp = new Bitmap(Image.FromStream(stream), new Size(32, 32)))
                    {
                        IntPtr hIcon = bmp.GetHicon();
                        _trayIcon = Icon.FromHandle(hIcon);
                        return;
                    }
                }
                catch { }
            }

            string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard.ico");
            if (System.IO.File.Exists(icoPath))
            {
                try
                {
                    _trayIcon = new Icon(icoPath);
                    return;
                }
                catch { }
            }

            // The installed copy (%LOCALAPPDATA%\EarGuard) ships the exe alone: no loose PNG or
            // ICO sits beside it. The logo is still present - it is embedded in the executable
            // itself via /win32icon at compile time - so extract that before giving up on branding.
            // ExtractAssociatedIcon returns an owning Icon: it must stay alive for the lifetime of
            // the tray icon, so it is intentionally NOT wrapped in a using block.
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exePath))
                {
                    _trayIcon = Icon.ExtractAssociatedIcon(exePath);
                    if (_trayIcon != null) return;
                }
            }
            catch { }

            _trayIcon = CreateShieldIcon(Color.FromArgb(16, 163, 74), Color.White);
        }

        private void InitializeContextMenu()
        {
            _contextMenu = new ContextMenuStrip();

            Image logoImg = null;
            string pngPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard-transparent.png");
            if (!System.IO.File.Exists(pngPath))
            {
                pngPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard.png");
            }

            if (System.IO.File.Exists(pngPath))
            {
                try
                {
                    using (var stream = new System.IO.FileStream(pngPath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                    {
                        logoImg = new Bitmap(Image.FromStream(stream), new Size(18, 18));
                    }
                }
                catch { }
            }

            // Single, clean, branded Open EarGuard item with the EarGuard logo
            _openMenuItem = new ToolStripMenuItem("Open EarGuard")
            {
                Font = new Font(_contextMenu.Font.FontFamily, 9.5f, FontStyle.Bold),
                Image = logoImg
            };
            _openMenuItem.Click += (s, e) => { if (OpenRequested != null) OpenRequested(this, EventArgs.Empty); };

            _exitMenuItem = new ToolStripMenuItem("Exit EarGuard");
            _exitMenuItem.Click += (s, e) => { if (ExitRequested != null) ExitRequested(this, EventArgs.Empty); };

            _contextMenu.Items.Add(_openMenuItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_exitMenuItem);
        }

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "EarGuard | Active Protection",
                Visible = true,
                ContextMenuStrip = _contextMenu
            };

            _notifyIcon.DoubleClick += (s, e) =>
            {
                if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
            };

            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
                }
            };
        }

        public void UpdateStatus(int guardedDeviceCount)
        {
            if (_notifyIcon == null) return;

            string text = string.Format("EarGuard | Protection Active ({0} guarded)", guardedDeviceCount);
            if (text.Length > 63) text = text.Substring(0, 63);
            _notifyIcon.Text = text;
        }

        public void ShowClampedNotification(string deviceName, int oldVol, int clampedVol)
        {
            if (_notifyIcon == null) return;

            // Rate limit to once every 3 seconds to prevent notification spam during a volume storm.
            if ((DateTime.Now - _lastBalloonTime).TotalSeconds < 3.0) return;
            _lastBalloonTime = DateTime.Now;

            string title = "EarGuard lowered the volume";
            string message = string.Format(
                "{0}: lowered {1}% to {2}%.",
                string.IsNullOrEmpty(deviceName) ? "Your audio device" : deviceName,
                oldVol,
                clampedVol);

            ShowSilentBalloon(title, message);
        }

        public void ShowInfoNotification(string title, string message)
        {
            ShowSilentBalloon(title, message);
        }

        /// <summary>
        /// Displays an informational balloon tip with the system sound suppressed.
        ///
        /// If the shell call cannot be made, no balloon is shown at all. Failing loudly would
        /// defeat the purpose of a tool whose whole job is to keep unexpected sound out of the
        /// user's ears; the same information is already visible in the main window.
        /// </summary>
        private void ShowSilentBalloon(string title, string message)
        {
            if (_notifyIcon == null) return;

            try
            {
                FieldInfo windowField = typeof(NotifyIcon).GetField(
                    "window", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo idField = typeof(NotifyIcon).GetField(
                    "id", BindingFlags.NonPublic | BindingFlags.Instance);
                if (windowField == null || idField == null) return;

                var nativeWindow = windowField.GetValue(_notifyIcon) as NativeWindow;
                if (nativeWindow == null || nativeWindow.Handle == IntPtr.Zero) return;

                var data = new NOTIFYICONDATA();
                data.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
                data.hWnd = nativeWindow.Handle;
                data.uID = (int)idField.GetValue(_notifyIcon);
                data.uFlags = NIF_INFO;
                data.szInfo = Truncate(message, 255);
                data.szInfoTitle = Truncate(title, 63);
                data.dwInfoFlags = NIIF_NONE | NIIF_NOSOUND | NIIF_RESPECT_QUIET_TIME;

                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
            catch
            {
                // Never let a cosmetic notification failure surface to the user.
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static Icon CreateShieldIcon(Color fillColor, Color symbolColor)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(16, 2, 28, 6);
                        path.AddLine(28, 6, 28, 16);
                        path.AddBezier(28, 16, 27, 25, 20, 29, 16, 31);
                        path.AddBezier(16, 31, 12, 29, 5, 25, 4, 16);
                        path.AddLine(4, 16, 4, 6);
                        path.CloseFigure();

                        using (var brush = new SolidBrush(fillColor))
                        {
                            g.FillPath(brush, path);
                        }

                        using (var pen = new Pen(Color.FromArgb(200, Color.White), 1.5f))
                        {
                            g.DrawPath(pen, path);
                        }
                    }

                    using (var symPen = new Pen(symbolColor, 2.5f))
                    {
                        symPen.StartCap = LineCap.Round;
                        symPen.EndCap = LineCap.Round;
                        g.DrawLine(symPen, 10, 16, 14, 21);
                        g.DrawLine(symPen, 14, 21, 22, 11);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        public void Dispose()
        {
            if (_taskbarListener != null)
            {
                try { _taskbarListener.Dispose(); } catch { }
                _taskbarListener = null;
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            if (_contextMenu != null)
            {
                _contextMenu.Dispose();
                _contextMenu = null;
            }

            if (_trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
    }
}
