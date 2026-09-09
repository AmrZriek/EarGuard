using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace EarGuard.Tray
{
    public class SingleInstance : IDisposable
    {
        private const string MutexName = @"Global\EarGuard_SingleInstance_Mutex";
        private const string WindowMessageName = "EarGuard_RestoreWindow_Msg";

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        private Mutex _mutex;
        private bool _hasHandle;
        private uint _restoreMsgId;

        public uint RestoreMessageId
        {
            get { return _restoreMsgId; }
        }

        public SingleInstance()
        {
            _restoreMsgId = RegisterWindowMessage(WindowMessageName);
        }

        public bool TryAcquire()
        {
            try
            {
                bool createdNew;
                _mutex = new Mutex(true, MutexName, out createdNew);
                _hasHandle = createdNew;
                return _hasHandle;
            }
            catch (Exception)
            {
                _hasHandle = false;
                return false;
            }
        }

        public void NotifyExistingInstance()
        {
            if (_restoreMsgId != 0)
            {
                PostMessage(HWND_BROADCAST, _restoreMsgId, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            if (_mutex != null)
            {
                if (_hasHandle)
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                }
                _mutex.Dispose();
                _mutex = null;
                _hasHandle = false;
            }
        }
    }
}
