using System;

namespace EarGuard.Tests
{
    public class TestRunner
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("        EarGuard Test Runner            ");
            Console.WriteLine("========================================");

            try
            {
                ConfigStoreTests.RunAll();
                ReliabilityTests.RunAll();
                TaskSchedulerStartupRegistrarTests.RunAll();
                AudioEngineTests.RunAll();
                TrayIconRecoveryTests.RunAll();
                IconOwnershipTests.RunAll();
                MainWindowTests.RunAll();
                if (Array.IndexOf(args, "--hardware") >= 0) EndToEndIntegrationTest.Run();
                Console.WriteLine("Retained artifacts: " + TestEnvironment.Root);
                Console.WriteLine();
                Console.WriteLine("========================================");
                Console.WriteLine("  ✓ ALL TEST SUITES PASSED SUCCESSFULLY ");
                Console.WriteLine("========================================");
                return 0;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine();
                Console.WriteLine("========================================");
                Console.WriteLine("  ✗ TEST RUN FAILED");
                Console.WriteLine("========================================");
                Console.WriteLine(ex.ToString());
                Console.ResetColor();
                return 1;
            }
        }
    }
}
