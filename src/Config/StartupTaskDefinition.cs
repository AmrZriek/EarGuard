using System;
using System.IO;

namespace EarGuard.Config
{
    internal sealed class StartupTaskDefinition
    {
        public string TaskName { get; private set; }
        public string ExecutablePath { get; private set; }
        public string Arguments { get; private set; }
        public string WorkingDirectory { get; private set; }
        public bool TriggerLogon { get; private set; }
        public TimeSpan Delay { get; private set; }
        public bool InteractiveOnly { get; private set; }
        public bool LimitedPrivilege { get; private set; }

        private StartupTaskDefinition()
        {
        }

        public static StartupTaskDefinition Build(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) throw new ArgumentNullException("executablePath");
            string absolutePath = Path.GetFullPath(executablePath);
            string workingDirectory = Path.GetDirectoryName(absolutePath);
            if (string.IsNullOrEmpty(workingDirectory)) workingDirectory = absolutePath;

            return new StartupTaskDefinition
            {
                TaskName = "EarGuardStartup",
                ExecutablePath = absolutePath,
                Arguments = "--tray",
                WorkingDirectory = workingDirectory,
                TriggerLogon = true,
                Delay = TimeSpan.Zero,
                InteractiveOnly = true,
                LimitedPrivilege = true
            };
        }
    }
}
