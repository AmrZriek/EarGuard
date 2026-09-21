namespace EarGuard.Config
{
    /// <summary>
    /// Resolves the absolute executable path that logon startup must launch.
    ///
    /// The registered task has to name a fixed path. Naming wherever the user happened to launch
    /// EarGuard from - a Downloads folder, a build tree, a temp extraction - means any later
    /// cleanup of that location silently disables startup at the next sign-in. Implementations
    /// therefore move the running build to a stable per-user location before returning it.
    /// </summary>
    internal interface IApplicationInstaller
    {
        /// <summary>
        /// Returns the path to register for logon startup, copying the running build to a durable
        /// location when it is not already there. Never returns null unless
        /// <paramref name="runningExecutablePath"/> is unusable.
        /// </summary>
        string ResolveStartupExecutable(string runningExecutablePath);
    }
}
