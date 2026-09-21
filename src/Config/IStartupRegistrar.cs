using System;

namespace EarGuard.Config
{
    internal interface IStartupRegistrar
    {
        bool IsEnabled();

        /// <summary>
        /// Reports whether startup is enabled *and* already launches
        /// <paramref name="executablePath"/>. Distinguishes "registered" from "registered against
        /// a path that still exists", which is how a stale registration is detected and repaired.
        /// </summary>
        bool IsEnabledFor(string executablePath);

        bool Enable(string executablePath);

        bool Disable();
    }

    internal interface ILegacyStartupRegistration
    {
        bool IsEnabled();
        bool Remove();
    }
}
