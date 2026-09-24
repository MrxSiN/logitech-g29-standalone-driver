using System;

namespace G29.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
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
                NativeVmPass();
                Console.WriteLine("ALL G29 TESTS PASSED. THE PROTOCOL GLYPHS REMAIN CONSISTENT.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
        }

        // The same scenarios again, with the application program running on the
        // native bridge's VM instead of the C# VM: both must produce identical
        // frames for every recorded behavior.
        private static void NativeVmPass()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            NativeVm.Use(System.IO.File.ReadAllText(System.IO.Path.Combine(BfMainTests.RepositoryRoot, "src", "brainfuck", "g29-main.bf")));
            try
            {
                Assert.True(NativeVm.IdiomCount == BfMainTests.Program.IdiomCount && NativeVm.MulAddCount == BfMainTests.Program.MulAddCount, "both VMs compile the program to the same superinstructions");
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
                NativeVm.Stop();
            }

            Console.WriteLine("  native VM: every scenario reproduced, {0} ms", stopwatch.ElapsedMilliseconds);
        }
    }
}
