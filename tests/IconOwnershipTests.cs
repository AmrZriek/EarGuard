using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using EarGuard.Tray;

namespace EarGuard.Tests
{
    internal static class IconOwnershipTests
    {
        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, int flags);

        internal static void RunAll()
        {
            string corruptLogo = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EarGuard-transparent.png");
            if (System.IO.File.Exists(corruptLogo)) throw new Exception("Corrupt-logo test requires an isolated build directory");
            System.IO.File.WriteAllText(corruptLogo, "This is not an image");
            try
            {
                using (var fallback = TrayManager.LoadLogoIcon(new Size(32, 32)))
                    if (fallback == null) throw new Exception("Corrupt logo must fall back to a usable icon");
            }
            finally { System.IO.File.Move(corruptLogo, corruptLogo + ".retained-" + Guid.NewGuid().ToString("N")); }
            using (var bitmap = new Bitmap(32, 32))
            using (var icon = TrayManager.OwnedIcon(bitmap))
            using (var copy = icon.ToBitmap())
                if (copy.Width != 32) throw new Exception("Owned icon must survive destruction of its original handle");

            // Warm up GDI+ before measuring retained native handles. No tray windows or balloons.
            Cycle();
            using (var process = Process.GetCurrentProcess())
            {
                int before = GetGuiResources(process.Handle, 0) + GetGuiResources(process.Handle, 1);
                for (int i = 0; i < 100; i++) Cycle();
                int after = GetGuiResources(process.Handle, 0) + GetGuiResources(process.Handle, 1);
                if (after > before) throw new Exception("Repeated icon creation/disposal leaked native handles: " + (after - before));
            }
            Console.WriteLine("[PASS] Owned icon lifetime and native handle stability (100 cycles)");
        }

        private static void Cycle()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var generated = TrayManager.OwnedIcon(bitmap))
            using (var resolved = TrayManager.LoadLogoIcon(new Size(32, 32)))
            using (var image = resolved.ToBitmap()) { }
        }
    }
}
