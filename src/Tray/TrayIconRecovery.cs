using System;

namespace EarGuard.Tray
{
    internal interface ITrayIconVisibility
    {
        bool Visible { get; set; }
    }

    internal static class TrayIconRecovery
    {
        public static void ReaddAfterTaskbarCreated(ITrayIconVisibility icon)
        {
            if (icon == null) return;
            icon.Visible = false;
            icon.Visible = true;
        }
    }
}
