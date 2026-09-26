using System;

namespace G29.Tests
{
    internal static class Program
    {
        private static int Main(string[] arguments)
        {
            try
            {
                if (arguments.Length == 1 && arguments[0] == "--benchmark")
                {
                    return Benchmark.Run();
                }

                if (arguments.Length == 2 && arguments[0] == "--write-programs")
                {
                    TestPrograms.Write(arguments[1]);
                    return 0;
                }

                ArchitectureTests.Run();
                AotCompilerTests.Run();
                ProtocolTests.Run();
                ReferenceReplay.Run();
                RuntimeTests.Run();
                BfMainTests.Run();
                CliParityTests.Run();
                RegistrationTests.Run();
                BridgeTests.Run();
                MonitorParityTests.Run();
                EngineParityTests.Run();
                SteeringParityTests.Run();
                ReaderParityTests.Run();
                DirectInputTests.Run();
                EffectDriverShellTests.Run();
                WatchdogTests.Run();
                SelectionParityTests.Run();
                InstallPlanTests.Run();
                SafetyTests.Run();
                CompiledPass();
                Console.WriteLine("All G29 tests passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
        }

        // The same scenarios again, with the application program compiled ahead
        // of time (the shipping code path) instead of the reference interpreter:
        // both must produce identical frames for every recorded behavior.
        private static void CompiledPass()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            AotMachine.Use();
            try
            {
                BfMainTests.Run();
                ProtocolTests.Run();
                ReferenceReplay.Run();
                CliParityTests.Run();
                RegistrationTests.Run();
                MonitorParityTests.Run();
                EngineParityTests.Run();
                SteeringParityTests.Run();
                ReaderParityTests.Run();
                DirectInputTests.Run();
                WatchdogTests.Run();
                SelectionParityTests.Run();
                InstallPlanTests.Run();
            }
            finally
            {
                AotMachine.Stop();
            }

            Console.WriteLine("  compiled program: every scenario reproduced, {0} ms, at most {1} loop iterations between two reads", stopwatch.ElapsedMilliseconds, AotMachine.PeakIterations);
            // The budget (bfrt.h) must stay far above every legitimate stretch.
            Assert.True(AotMachine.PeakIterations > 0 && AotMachine.PeakIterations * 20 < SafetyTests.IterationBudget, "the iteration budget is at least 20 times the worst measured stretch");
        }
    }
}
