using System;
using System.Collections.Generic;
using EarGuard.Tray;

namespace EarGuard.Tests
{
    public static class TrayIconRecoveryTests
    {
        public static void RunAll()
        {
            Console.WriteLine("[TEST] Running TrayIconRecoveryTests...");
            Test_TaskbarCreated_ReaddsSameIconOnce();
            Console.WriteLine("[PASS] All TrayIconRecoveryTests passed!");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Assertion Failed: " + message);
            }
        }

        private static void Test_TaskbarCreated_ReaddsSameIconOnce()
        {
            var fake = new FakeTrayIcon { Visible = true };
            fake.VisibleTransitions.Clear();
            TrayIconRecovery.ReaddAfterTaskbarCreated(fake);

            Assert(fake.VisibleTransitions.Count == 2, "Recovery must toggle visibility exactly twice");
            Assert(fake.VisibleTransitions[0] == false, "Recovery must hide icon first");
            Assert(fake.VisibleTransitions[1] == true, "Recovery must show icon second");
            Assert(fake.Visible, "Icon must be visible after recovery");
            Console.WriteLine("  ✓ Test_TaskbarCreated_ReaddsSameIconOnce");
        }

        private sealed class FakeTrayIcon : ITrayIconVisibility
        {
            private bool _visible;
            public readonly List<bool> VisibleTransitions = new List<bool>();

            public bool Visible
            {
                get { return _visible; }
                set
                {
                    _visible = value;
                    VisibleTransitions.Add(value);
                }
            }
        }
    }
}
