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
                AudioEngineTests.RunAll();
                EndToEndIntegrationTest.Run();
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
