using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EarGuard.Config
{
    internal sealed class TaskSchedulerStartupRegistrar : IStartupRegistrar
    {
        private const int TASK_TRIGGER_LOGON = 9;
        private const int TASK_ACTION_EXEC = 0;
        private const int TASK_CREATE_OR_UPDATE = 6;
        private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
        private const int TASK_RUNLEVEL_LUA = 0;
        private const int TASK_INSTANCES_IGNORE_NEW = 2;

        private readonly Func<object> _connect;
        public TaskSchedulerStartupRegistrar() : this(Connect) { }
        internal TaskSchedulerStartupRegistrar(Func<object> connect) { _connect = connect; }

        public bool IsEnabled()
        {
            return Inspect(null);
        }

        public bool IsEnabledFor(string executablePath)
        {
            return !string.IsNullOrEmpty(executablePath) && Inspect(executablePath);
        }

        private bool Inspect(string executablePath)
        {
            object service = null, folder = null, task = null;
            try
            {
                service = _connect();
                folder = Invoke(service, "GetFolder", "\\");
                task = Invoke(folder, "GetTask", StartupTaskDefinition.Build("EarGuard.exe").TaskName);
                return (bool)Get(task, "Enabled") && (executablePath == null || IsHealthyTask(task, executablePath));
            }
            catch (TargetInvocationException) { return false; }
            catch (COMException) { return false; }
            catch (InvalidOperationException) { return false; }
            finally { Release(task); Release(folder); Release(service); }
        }

        internal static bool IsHealthyTask(object task, string executablePath)
        {
            object definition = null, settings = null, actions = null, action = null;
            object principal = null, triggers = null, trigger = null;
            try
            {
                if (!(bool)Get(task, "Enabled")) return false;
                definition = Get(task, "Definition");
                settings = Get(definition, "Settings");
                if (!(bool)Get(settings, "Enabled") || !(bool)Get(settings, "AllowDemandStart")
                    || !(bool)Get(settings, "StartWhenAvailable") || (bool)Get(settings, "DisallowStartIfOnBatteries")
                    || (bool)Get(settings, "StopIfGoingOnBatteries")
                    || (int)Get(settings, "MultipleInstances") != TASK_INSTANCES_IGNORE_NEW
                    || (string)Get(settings, "ExecutionTimeLimit") != "PT0S") return false;
                principal = Get(definition, "Principal");
                if ((int)Get(principal, "LogonType") != TASK_LOGON_INTERACTIVE_TOKEN
                    || (int)Get(principal, "RunLevel") != TASK_RUNLEVEL_LUA
                    || !IsCurrentUser((string)Get(principal, "UserId"))) return false;
                triggers = Get(definition, "Triggers");
                if ((int)Get(triggers, "Count") != 1) return false;
                trigger = Item(triggers, 1);
                string delay = (string)Get(trigger, "Delay");
                if ((int)Get(trigger, "Type") != TASK_TRIGGER_LOGON || !(bool)Get(trigger, "Enabled")
                    || (!string.IsNullOrEmpty(delay) && delay != "PT0S")
                    || !IsCurrentUser((string)Get(trigger, "UserId"))) return false;
                actions = Get(definition, "Actions");
                if ((int)Get(actions, "Count") != 1) return false;
                action = Item(actions, 1);
                var expected = StartupTaskDefinition.Build(executablePath);
                return (int)Get(action, "Type") == TASK_ACTION_EXEC
                    && string.Equals((string)Get(action, "Path"), expected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    && (string)Get(action, "Arguments") == expected.Arguments
                    && string.Equals((string)Get(action, "WorkingDirectory"), expected.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Release(trigger); Release(triggers); Release(principal); Release(action);
                Release(actions); Release(settings); Release(definition);
            }
        }

        private static bool IsCurrentUser(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return false;
            // Scheduler may return a SID, a qualified account, or a short account name.
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                if (identity.User == null) return false;
                if (string.Equals(userId, identity.User.Value, StringComparison.OrdinalIgnoreCase)) return true;
                try
                {
                    var account = new System.Security.Principal.NTAccount(userId);
                    return identity.User.Equals(account.Translate(typeof(System.Security.Principal.SecurityIdentifier)));
                }
                catch (System.Security.Principal.IdentityNotMappedException) { return false; }
                catch (ArgumentException) { return false; }
            }
        }

        private static object Item(object collection, int index)
        {
            return collection.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, collection, new object[] { index });
        }

        public bool Enable(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) return false;

            StartupTaskDefinition definition;
            try
            {
                definition = StartupTaskDefinition.Build(executablePath);
            }
            catch
            {
                return false;
            }

            object service = null;
            try
            {
                service = _connect();
                object folder = Invoke(service, "GetFolder", "\\");
                try
                {
                    object taskDefinition = Invoke(service, "NewTask", 0);
                    try
                    {
                        Configure(taskDefinition, definition);
                        object registered = Invoke(folder, "RegisterTaskDefinition",
                            definition.TaskName,
                            taskDefinition,
                            TASK_CREATE_OR_UPDATE,
                            null,
                            null,
                            TASK_LOGON_INTERACTIVE_TOKEN,
                            null);
                        Release(registered);
                    }
                    finally
                    {
                        Release(taskDefinition);
                    }
                    return true;
                }
                finally
                {
                    Release(folder);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(service);
            }
        }

        public bool Disable()
        {
            object service = null;
            try
            {
                service = _connect();
                object folder = Invoke(service, "GetFolder", "\\");
                try
                {
                    try
                    {
                        Invoke(folder, "DeleteTask", StartupTaskDefinition.Build("EarGuard.exe").TaskName, 0);
                    }
                    catch (TargetInvocationException ex)
                    {
                        if (!IsNotFound(ex)) throw;
                    }
                    return true;
                }
                finally
                {
                    Release(folder);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(service);
            }
        }

        private static void Configure(object taskDefinition, StartupTaskDefinition definition)
        {
            string currentUser = Environment.UserDomainName + "\\" + Environment.UserName;

            object registrationInfo = Get(taskDefinition, "RegistrationInfo");
            try
            {
                Set(registrationInfo, "Author", "EarGuard");
                Set(registrationInfo, "Description", "Start EarGuard at user logon with no delay.");
            }
            finally
            {
                Release(registrationInfo);
            }

            object principal = Get(taskDefinition, "Principal");
            try
            {
                Set(principal, "UserId", currentUser);
                Set(principal, "LogonType", TASK_LOGON_INTERACTIVE_TOKEN);
                Set(principal, "RunLevel", TASK_RUNLEVEL_LUA);
            }
            finally
            {
                Release(principal);
            }

            object settings = Get(taskDefinition, "Settings");
            try
            {
                Set(settings, "Enabled", true);
                Set(settings, "ExecutionTimeLimit", "PT0S");
                Set(settings, "AllowDemandStart", true);
                Set(settings, "StartWhenAvailable", true);
                Set(settings, "DisallowStartIfOnBatteries", false);
                Set(settings, "StopIfGoingOnBatteries", false);
                Set(settings, "MultipleInstances", TASK_INSTANCES_IGNORE_NEW);
            }
            finally
            {
                Release(settings);
            }

            object triggers = Get(taskDefinition, "Triggers");
            try
            {
                object trigger = Invoke(triggers, "Create", TASK_TRIGGER_LOGON);
                try
                {
                    Set(trigger, "UserId", currentUser);
                    Set(trigger, "Enabled", true);
                    Set(trigger, "Delay", "PT0S");
                }
                finally
                {
                    Release(trigger);
                }
            }
            finally
            {
                Release(triggers);
            }

            object actions = Get(taskDefinition, "Actions");
            try
            {
                object exec = Invoke(actions, "Create", TASK_ACTION_EXEC);
                try
                {
                    Set(exec, "Path", definition.ExecutablePath);
                    Set(exec, "Arguments", definition.Arguments);
                    Set(exec, "WorkingDirectory", definition.WorkingDirectory);
                }
                finally
                {
                    Release(exec);
                }
            }
            finally
            {
                Release(actions);
            }
        }

        private static object Connect()
        {
            Type serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null) throw new InvalidOperationException("Task Scheduler is unavailable.");
            object service = Activator.CreateInstance(serviceType);
            try { Invoke(service, "Connect", new object[0]); return service; }
            catch { Release(service); throw; }
        }

        private static bool IsNotFound(TargetInvocationException ex)
        {
            if (ex == null || ex.InnerException == null) return false;
            var com = ex.InnerException as COMException;
            if (com == null) return false;
            return ((uint)com.ErrorCode == 0x80070002);
        }

        private static object Invoke(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(
                name,
                BindingFlags.InvokeMethod,
                null,
                target,
                args);
        }

        private static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(
                name,
                BindingFlags.GetProperty,
                null,
                target,
                null);
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().InvokeMember(
                name,
                BindingFlags.SetProperty,
                null,
                target,
                new object[] { value });
        }

        private static void Release(object comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch { }
        }
    }
}
