using System;
using EarGuard.Config;

namespace EarGuard.Tests
{
    public static class TaskSchedulerStartupRegistrarTests
    {
        public static void RunAll()
        {
            Test_NormalizedTaskRemainsHealthy();
            var service = new Service();
            var registrar = new TaskSchedulerStartupRegistrar(() => service);
            string path = System.IO.Path.Combine(TestEnvironment.Root, "EarGuard.exe");
            Check(registrar.Enable(path), "Enable must traverse and configure the task definition");
            Check(service.Folder.Registrations == 1 && service.Task.Definition.Settings.ExecutionTimeLimit == "PT0S",
                "Registered task must have unlimited runtime");
            Check(registrar.IsEnabledFor(path), "Healthy Definition.Actions task must pass actual traversal");
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                service.Task.Definition.Principal.UserId = identity.User.Value;
                service.Task.Definition.Triggers.Value.UserId = identity.User.Value;
            }
            Check(registrar.IsEnabledFor(path), "Scheduler-normalized user SIDs must remain healthy");
            service.Task.Enabled = false;
            Check(!registrar.IsEnabled() && !registrar.IsEnabledFor(path), "Disabled task must be reported accurately");
            service.Task.Enabled = true;
            service.Task.Definition.Actions.Value.Arguments = "";
            Check(!registrar.IsEnabledFor(path), "Missing tray argument must require repair");
            registrar.Enable(path);
            service.Task.Definition.Settings.ExecutionTimeLimit = "PT72H";
            Check(!registrar.IsEnabledFor(path), "Default runtime limit must require repair");
            registrar.Enable(path);
            service.Task.Definition.Actions.Value.Path = path + ".old";
            Check(!registrar.IsEnabledFor(path), "Stale executable must require repair");
            Console.WriteLine("[PASS] Actual Task Scheduler object traversal and settings regressions");
        }

        private static void Test_NormalizedTaskRemainsHealthy()
        {
            var service = new Service();
            var registrar = new TaskSchedulerStartupRegistrar(() => service);
            string path = System.IO.Path.Combine(TestEnvironment.Root, "Normalized-EarGuard.exe");
            Check(registrar.Enable(path), "Normalized-task fixture must register");
            service.Task.Definition.Principal.UserId = Environment.UserName;
            Check(registrar.IsEnabledFor(path), "Windows short principal name must resolve to the current user");
            service.Task.Definition.Triggers.Value.UserId = Environment.UserName;
            foreach (string delay in new[] { "", null, "PT0S" })
            {
                service.Task.Definition.Triggers.Value.Delay = delay;
                Check(registrar.IsEnabledFor(path), "Absent and explicit zero trigger delays must remain healthy");
            }
            var config = EarGuardConfig.CreateDefault();
            config.LaunchOnStartup = true;
            var store = TestEnvironment.Store(registrar, executable: path);
            Check(store.MigrateStartupRegistrationIfNeeded(config) && service.Folder.Registrations == 1,
                "Migration must leave a Windows-normalized healthy task untouched");

            service.Task.Definition.Triggers.Value.Delay = "PT1S";
            Check(!registrar.IsEnabledFor(path), "Nonzero trigger delay must still require repair");
            service.Task.Definition.Triggers.Value.Delay = "";
            service.Task.Definition.Principal.UserId = "S-1-5-18";
            Check(!registrar.IsEnabledFor(path), "Another principal must not be accepted as the current user");
            service.Task.Definition.Principal.UserId = "EarGuard-Unmapped-" + Guid.NewGuid().ToString("N");
            Check(!registrar.IsEnabledFor(path), "Unresolvable account must report unhealthy without throwing");
            service.Task.Definition.Principal.UserId = Environment.UserName;
            service.Task.Definition.Triggers.Value.UserId = "S-1-5-18";
            Check(!registrar.IsEnabledFor(path), "Another user's logon trigger must still require repair");
            Console.WriteLine("  [PASS] Normalized task identity, zero delay and migration no-op regressions");
        }

        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

        // Deliberately no Actions member on Task: it only exists on Definition, as in COM.
        public sealed class Task
        {
            public bool Enabled { get; set; }
            public Definition Definition { get; set; }
        }
        public sealed class Service
        {
            public Task Task = new Task { Enabled = true };
            public Folder Folder;
            public Service() { Folder = new Folder(this); }
            public Folder GetFolder(string path) { return Folder; }
            public Definition NewTask(int flags) { return new Definition(); }
        }
        public sealed class Folder
        {
            private readonly Service _service;
            public int Registrations;
            public Folder(Service service) { _service = service; }
            public Task GetTask(string name) { return _service.Task; }
            public Task RegisterTaskDefinition(string name, Definition definition, int flags, object user, object password, int logon, object security)
            {
                Registrations++;
                _service.Task.Definition = definition;
                return _service.Task;
            }
        }
        public sealed class Definition
        {
            public Registration RegistrationInfo { get; private set; }
            public Principal Principal { get; private set; }
            public Settings Settings { get; private set; }
            public Actions Actions { get; private set; }
            public Triggers Triggers { get; private set; }
            public Definition()
            {
                RegistrationInfo = new Registration(); Principal = new Principal(); Settings = new Settings();
                Actions = new Actions(); Triggers = new Triggers();
            }
        }
        public sealed class Registration { public string Author { get; set; } public string Description { get; set; } }
        public sealed class Principal
        {
            public string UserId { get; set; }
            public int LogonType { get; set; }
            public int RunLevel { get; set; }
        }
        public sealed class Settings
        {
            public bool Enabled { get; set; }
            public bool AllowDemandStart { get; set; }
            public bool StartWhenAvailable { get; set; }
            public bool DisallowStartIfOnBatteries { get; set; }
            public bool StopIfGoingOnBatteries { get; set; }
            public int MultipleInstances { get; set; }
            public string ExecutionTimeLimit { get; set; }
        }
        public sealed class Actions
        {
            public Exec Value = new Exec();
            public int Count { get { return 1; } }
            public Exec this[int index] { get { return Value; } }
            public Exec Create(int type) { Value.Type = type; return Value; }
        }
        public sealed class Exec
        {
            public int Type { get; set; }
            public string Path { get; set; }
            public string Arguments { get; set; }
            public string WorkingDirectory { get; set; }
        }
        public sealed class Triggers
        {
            public Trigger Value = new Trigger();
            public int Count { get { return 1; } }
            public Trigger this[int index] { get { return Value; } }
            public Trigger Create(int type) { Value.Type = type; return Value; }
        }
        public sealed class Trigger
        {
            public int Type { get; set; }
            public string UserId { get; set; }
            public bool Enabled { get; set; }
            public string Delay { get; set; }
        }
    }
}
