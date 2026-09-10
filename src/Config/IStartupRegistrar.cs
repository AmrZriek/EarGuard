using System;

namespace EarGuard.Config
{
    internal interface IStartupRegistrar
    {
        bool IsEnabled();
        bool Enable(string executablePath);
        bool Disable();
    }

    internal interface ILegacyStartupRegistration
    {
        bool IsEnabled();
        bool Remove();
    }
}
