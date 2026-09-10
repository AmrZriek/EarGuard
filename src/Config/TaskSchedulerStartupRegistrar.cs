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

        public bool IsEnabled()
        {
            object service = null;
            try
            {
                service = Connect();
                object folder = Invoke(service, "GetFolder", "\\");
                try
                {
                    object task = Invoke(folder, "GetTask", StartupTaskDefinition.Build("EarGuard.exe").TaskName);
                    Release(task);
                    Release(folder);
                    return true;
                }
                catch
                {
                    Release(folder);
                    return false;
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
                service = Connect();
                object folder = Invoke(service, "GetFolder", "\\");
                try
                {
                    object taskDefinition = Invoke(service, "NewTask", 0);
                    try
                    {
                        Configure(taskDefinition, definition);
                        Invoke(folder, "RegisterTaskDefinition",
                            definition.TaskName,
                            taskDefinition,
                            TASK_CREATE_OR_UPDATE,
                            null,
                            null,
                            TASK_LOGON_INTERACTIVE_TOKEN,
                            null);
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
                service = Connect();
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
            Invoke(service, "Connect", new object[0]);
            return service;
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
