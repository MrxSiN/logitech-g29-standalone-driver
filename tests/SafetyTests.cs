using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The native safety envelope around the Brainfuck program, which the program
    // cannot change: the output guard's report allowlist, ceilings and trip
    // latch, the force lease (hold limit and stalled-program watchdog), the
    // registry capability allowlist, the command-frame parser, and the containment
    // of compiled programs against hostile code. Hardware-free: the guard's writes go
    // to a recording callback.
    internal static class SafetyTests
    {
        // bfrt.h BF_DEFAULT_ITERATION_BUDGET
        internal const long IterationBudget = BfVm.DefaultIterationBudget;

        private const int TapeLength = 131072;
        private static readonly byte[] StopReport = { 0x13, 0, 0, 0, 0, 0, 0 };

        internal static void Run()
        {
            ReportAllowlist();
            Ceilings();
            TripLatch();
            HoldLease();
            LeaseThread();
            RegistryAllowlist();
            FrameParser();
            HostileProgram();
            CompiledDifferentialFuzz();
            CompiledClosedForms();
            CompiledConcurrency();
            SessionWatchdog();
            Console.WriteLine("  safety envelope: guard, lease, registry, frames, compiled-program containment");
        }

        // ------------------------------------------------------------------
        // Output guard
        // ------------------------------------------------------------------

        // Every report the protocol can produce (tests/reference/protocol.txt is
        // exhaustive over the legal inputs).
        private static List<byte[]> ProtocolReports()
        {
            var reports = new List<byte[]>();
            foreach (string line in ReferenceFixtures.Lines("protocol.txt"))
            {
                if (line.StartsWith("identify", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string token in line.Split(' ').Skip(1))
                {
                    if (token.Length == 14)
                    {
                        reports.Add(ReferenceFixtures.Hex(token));
                    }
                }
            }

            return reports;
        }

        private static void ReportAllowlist()
        {
            List<byte[]> reports = ProtocolReports();
            Assert.True(reports.Count > 1000, "the protocol vectors were read");
            using (var guard = new NativeGuard(100, delegate { return 0; }))
            {
                foreach (byte[] report in reports)
                {
                    Assert.False(guard.Refuses(report), "the guard admits the protocol report " + ReferenceFixtures.Hex(report));
                }

                // Each family's fixed positions and the observed range of its
                // variable positions, derived from the vectors, not from guard.c.
                var families = new Dictionary<int, int[][]>();
                foreach (byte[] report in reports)
                {
                    int key = (report[0] << 8) | report[1];
                    int[][] range;
                    if (!families.TryGetValue(key, out range))
                    {
                        range = new int[7][];
                        for (int i = 0; i < 7; i++)
                        {
                            range[i] = new int[] { report[i], report[i] };
                        }

                        families[key] = range;
                    }

                    for (int i = 0; i < 7; i++)
                    {
                        range[i][0] = Math.Min(range[i][0], report[i]);
                        range[i][1] = Math.Max(range[i][1], report[i]);
                    }
                }

                // Mutated protocol reports: whatever the guard admits must have a
                // known family's shape.
                var random = new Random(29);
                int admitted = 0;
                for (int trial = 0; trial < 100000; trial++)
                {
                    byte[] candidate = (byte[])reports[random.Next(reports.Count)].Clone();
                    int changes = 1 + random.Next(2);
                    for (int c = 0; c < changes; c++)
                    {
                        candidate[random.Next(7)] = (byte)random.Next(256);
                    }

                    if (guard.Refuses(candidate))
                    {
                        continue;
                    }

                    admitted++;
                    int[][] family;
                    Assert.True(families.TryGetValue((candidate[0] << 8) | candidate[1], out family), "an admitted report belongs to a protocol family: " + ReferenceFixtures.Hex(candidate));
                    for (int i = 0; i < 7; i++)
                    {
                        Assert.True(candidate[i] >= family[i][0] && candidate[i] <= family[i][1], "an admitted report stays inside its family's observed bytes: " + ReferenceFixtures.Hex(candidate));
                    }
                }

                Assert.True(admitted > 1000, "the fuzzing reached admissible reports");

                string[] refused =
                {
                    "F8812700000000", // 39 degrees
                    "F8818503000000", // 901 degrees
                    "F8810000000000",
                    "F8812800000001", // trailing byte
                    "F8122000000000", // LED mask 32
                    "FE0D0808000000", // autocenter step 8
                    "FE0D0102000000", // mismatched autocenter bytes
                    "FE0D0000010001",
                    "11088081000000", // constant force with a changed fourth byte
                    "1108FF80000001",
                    "2108FF80000000", // constant force in another slot
                    "F108FF80000000", // constant force in every slot
                    "1100FF80000000",
                    "13000000000001",
                    "14010000000000",
                    "F5000000000100",
                    "F8090501010001",
                    "F8090501020000",
                    "F80A0000000001",
                    "F8FF0000000000",
                    "00000000000000",
                    "FFFFFFFFFFFFFF"
                };
                foreach (string hex in refused)
                {
                    Assert.True(guard.Refuses(ReferenceFixtures.Hex(hex)), "the guard refuses " + hex);
                }

                Assert.True(guard.Refuses(new byte[0]), "an empty report is refused");
                Assert.True(guard.Refuses(new byte[] { 0x13, 0, 0, 0, 0, 0 }), "a six-byte stop report is refused");
                Assert.True(guard.Refuses(new byte[] { 0x13, 0, 0, 0, 0, 0, 0, 0 }), "an eight-byte stop report is refused");
            }
        }

        private static void Ceilings()
        {
            using (var service = new NativeGuard(0, delegate { return 0; }))
            {
                Assert.False(service.Refuses(Force(0x80)), "a zero ceiling admits a zero force");
                Assert.True(service.Refuses(Force(0x81)) && service.Refuses(Force(0x7F)), "a zero ceiling refuses the smallest force");
                Assert.False(service.Refuses(StopReport), "a zero ceiling admits the stop report");
            }

            using (var guard = new NativeGuard(100, delegate { return 0; }))
            {
                Assert.False(guard.Refuses(Force(0xFF)), "full scale before lowering");
                Assert.Equal(0, guard.Lower(25), "the ceiling can be lowered");
                Assert.True(guard.Refuses(Force(0x80 + 33)) && !guard.Refuses(Force(0x80 + 32)), "the lowered ceiling is 25 %");
                Assert.Equal(-1, guard.Lower(100), "the ceiling cannot be raised again");
                Assert.Equal(-1, guard.Lower(101), "an impossible ceiling is refused");
                Assert.True(guard.Refuses(Force(0xFF)), "still 25 % after a refused raise");
                Assert.Equal(0, guard.Lower(0), "the ceiling can go to zero");
                Assert.True(guard.Refuses(Force(0x81)), "zero ceiling");
            }
        }

        private static void TripLatch()
        {
            var writes = new List<string>();
            using (var guard = new NativeGuard(100, delegate(string path, int length, byte[] payload)
            {
                writes.Add(path + " " + ReferenceFixtures.Hex(payload));
                return 0;
            }))
            {
                guard.Written("wheel-a", 8, Force(0x90));
                guard.Trip();
                Assert.True(writes.Count == 1 && writes[0] == "wheel-a 13000000000000", "the trip stops the held force");
                Assert.True(guard.Refuses(Force(0x90)) && guard.Refuses(Force(0x70)), "after a trip every non-zero force is refused");
                Assert.False(guard.Refuses(Force(0x80)), "a zero force still passes after a trip");
                Assert.False(guard.Refuses(StopReport), "the stop report still passes after a trip");
                Assert.False(guard.Refuses(ReferenceFixtures.Hex("F8120500000000")), "non-force reports still pass after a trip");

                // a force checked before the trip and written after it
                guard.Written("wheel-b", 8, Force(0x70));
                Assert.True(writes.Count == 2 && writes[1] == "wheel-b 13000000000000", "a force written after the trip is stopped at once");
                Assert.False(guard.AnyForce, "nothing is remembered as held after a trip");
            }
        }

        private static void HoldLease()
        {
            using (var guard = new NativeGuard(100, delegate { return 0; }))
            {
                guard.FakeClock(1000);
                Assert.Equal(0, (int)guard.HeldMs(1000), "nothing held");
                guard.Written("wheel", 8, Force(0xA0));
                Assert.Equal(1, (int)guard.HeldMs(1000), "a force begun now counts as held");
                guard.FakeClock(3000);
                guard.Written("wheel", 8, Force(0xB0));
                Assert.Equal(6001, (int)guard.HeldMs(7001), "a changed force continues the same hold");
                Assert.Equal(0, guard.Evaluate(IntPtr.Zero, 6000, 0, 7000), "the lease holds up to its limit");
                Assert.Equal(1, guard.Evaluate(IntPtr.Zero, 6000, 0, 7001), "the lease expires past its limit");
                Assert.Equal(0, guard.Evaluate(IntPtr.Zero, 0, 0, 1000000), "no limit, no expiry");
                guard.Written("wheel", 8, Force(0x80));
                Assert.Equal(0, (int)guard.HeldMs(9000), "a zero force ends the hold");
                Assert.Equal(0, guard.Evaluate(IntPtr.Zero, 1, 0, 1000000), "nothing held, nothing expires");
                guard.FakeClock(10000);
                guard.Written("wheel", 8, Force(0x60));
                Assert.Equal(1, (int)guard.HeldMs(10000), "a new hold starts again");
            }
        }

        private static void LeaseThread()
        {
            var writes = new List<string>();
            var expired = new ManualResetEvent(false);
            string reason = null;
            using (var guard = new NativeGuard(100, delegate(string path, int length, byte[] payload)
            {
                lock (writes)
                {
                    writes.Add(path + " " + ReferenceFixtures.Hex(payload));
                }

                return 0;
            }))
            {
                LeaseExpired callback = delegate(string text)
                {
                    reason = text;
                    expired.Set();
                };
                guard.Written("wheel", 8, Force(0xFF));
                IntPtr lease = bft_lease_start(guard.Handle, IntPtr.Zero, 150, 0, callback);
                Assert.True(lease != IntPtr.Zero, "the lease starts");
                try
                {
                    Assert.True(expired.WaitOne(3000), "the lease expires on its own thread, whatever the program does");
                    Assert.True(reason != null && reason.Contains("held longer"), "the expiry names the hold limit");
                    lock (writes)
                    {
                        Assert.True(writes.Count == 1 && writes[0] == "wheel 13000000000000", "the expiry sends the stop report");
                    }

                    Assert.True(guard.Refuses(Force(0xFF)), "the expiry trips the guard");
                }
                finally
                {
                    bft_lease_stop(lease);
                    GC.KeepAlive(callback);
                }
            }
        }

        // ------------------------------------------------------------------
        // Registry capability
        // ------------------------------------------------------------------

        private static void RegistryAllowlist()
        {
            string[] allowed =
            {
                @"SOFTWARE\Classes\CLSID\{D252A2D4-A917-47D3-BD1B-F5A0138CFE12}",
                @"software\classes\clsid\{d252a2d4-a917-47d3-bd1b-f5a0138cfe12}\InprocServer32",
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C24F",
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C24F\OEMForceFeedback\Effects\{13541C20-8E33-11D0-9AD0-00A0C9A06E35}",
                @"Software\G29Standalone",
                @"Software\G29Standalone\DirectInput"
            };
            string[] refused =
            {
                "",
                @"SOFTWARE\Classes\CLSID",
                @"SOFTWARE\Classes\CLSID\{D252A2D4-A917-47D3-BD1B-F5A0138CFE13}",
                @"SOFTWARE\Classes\CLSID\{D252A2D4-A917-47D3-BD1B-F5A0138CFE12}X",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"System\CurrentControlSet\Services\Spooler",
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C24E",
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM",
                @"Software\G29StandaloneX",
                @"Software\G29Standalone\\DirectInput",
                @"Software\G29Standalone\",
                @"\Software\G29Standalone"
            };
            foreach (string path in allowed)
            {
                Assert.True(bft_registry_writable(path) != 0, "the program may change " + path);
            }

            foreach (string path in refused)
            {
                Assert.True(bft_registry_writable(path) == 0, "the program may not change '" + path + "'");
            }

            Assert.True(FakeRegistry.MutatedPaths.Count > 5, "the registration scenarios changed the registry");
            foreach (string path in FakeRegistry.MutatedPaths)
            {
                Assert.True(bft_registry_writable(path) != 0, "every key the program changes in the scenarios is inside the native allowlist: " + path);
            }
        }

        // ------------------------------------------------------------------
        // Command frames
        // ------------------------------------------------------------------

        internal static byte[] Frame(int type, int flags, int sequence, byte[] payload)
        {
            var bytes = new byte[8 + payload.Length];
            bytes[0] = 0xA5;
            bytes[1] = 1;
            bytes[2] = (byte)type;
            bytes[3] = (byte)flags;
            bytes[4] = (byte)sequence;
            bytes[5] = (byte)(sequence >> 8);
            bytes[6] = (byte)payload.Length;
            bytes[7] = (byte)(payload.Length >> 8);
            Array.Copy(payload, 0, bytes, 8, payload.Length);
            return bytes;
        }

        private static int Parse(byte[] bytes, int[] last)
        {
            return bft_parse_frames(bytes, bytes.Length, last);
        }

        private static void FrameParser()
        {
            var last = new int[4];
            Assert.Equal(1, Parse(Frame(0x81, 0, 0x1234, new byte[] { 1, 2, 3 }), last), "one frame");
            Assert.True(last[0] == 0x81 && last[1] == 0 && last[2] == 0x1234 && last[3] == 3, "the frame's fields");
            Assert.Equal(1, Parse(Frame(0x93, 0, 7, new byte[4096]), last), "the largest payload");
            Assert.Equal(1, Parse(Frame(0x80, 0, 0, new byte[0]), last), "an empty payload");

            byte[] bad = Frame(0x81, 0, 0, new byte[1]);
            bad[0] = 0xA4;
            Assert.Equal(-1, Parse(bad, last), "bad magic is refused at its byte");
            bad = Frame(0x81, 0, 0, new byte[1]);
            bad[1] = 2;
            Assert.Equal(-2, Parse(bad, last), "an unknown ABI version is refused at its byte");
            // magic and version are checked per byte, the rest once the header is complete
            Assert.Equal(-8, Parse(Frame(0x81, 1, 0, new byte[1]), last), "reserved flags are refused");
            bad = Frame(0x81, 0, 0, new byte[0]);
            bad[6] = 0x01;
            bad[7] = 0x10; // 4097
            Assert.Equal(-8, Parse(bad, last), "a payload over 4096 bytes is refused at the length");

            byte[] two = Frame(0x81, 0, 1, new byte[] { 9 }).Concat(Frame(0x82, 0, 2, new byte[] { 1, 2 })).ToArray();
            Assert.Equal(2, Parse(two, last), "frames follow each other");
            Assert.Equal(1, Parse(two.Take(two.Length - 1).ToArray(), new int[4]), "a truncated frame is not delivered");

            // Fuzz: arbitrary bytes and mutated frame streams. The parser must never
            // read outside what it was given (the test DLL would crash), must stop at
            // the first violation, and must deliver exactly the frames of a valid stream.
            var random = new Random(4096);
            for (int trial = 0; trial < 20000; trial++)
            {
                var stream = new List<byte>();
                int frames = random.Next(4);
                for (int f = 0; f < frames; f++)
                {
                    var payload = new byte[random.Next(3) == 0 ? random.Next(4097) : random.Next(16)];
                    random.NextBytes(payload);
                    stream.AddRange(Frame(random.Next(256), 0, random.Next(65536), payload));
                }

                byte[] bytes = stream.ToArray();
                if (trial % 2 == 0)
                {
                    Assert.Equal(frames, Parse(bytes, last), "a valid stream delivers every frame");
                }

                if (bytes.Length > 0)
                {
                    int changes = 1 + random.Next(3);
                    for (int c = 0; c < changes; c++)
                    {
                        bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                    }
                }
                else
                {
                    bytes = new byte[random.Next(64)];
                    random.NextBytes(bytes);
                }

                int result = Parse(bytes, last);
                Assert.True(result >= -bytes.Length && result <= bytes.Length / 8 + 1, "the parser's answer stays inside the input");
                if (result < 0)
                {
                    int index = -result - 1;
                    byte[] prefix = bytes.Take(index).ToArray();
                    Assert.True(Parse(prefix, new int[4]) >= 0, "everything before the violation parses");
                }
            }
        }

        // ------------------------------------------------------------------
        // Containment of compiled programs
        // ------------------------------------------------------------------

        private sealed class Outcome
        {
            internal int Result; // 1 finished, 0 stopped (input ended), -1 fault
            internal string Fault = string.Empty;
            internal readonly List<byte> Output = new List<byte>();
            internal long Iterations;
        }

        // Runs a corpus program (TestPrograms) compiled ahead of time. input: the
        // bytes read before input ends (-1); refuseAfter: output bytes accepted
        // before the host refuses more; preset: cell 0 before the run.
        private static Outcome RunCompiled(string source, byte[] input, long budget, int refuseAfter = int.MaxValue, int? preset = null)
        {
            var outcome = new Outcome();
            int position = 0;
            var machine = new AotMachine(
                AotMachine.Program(TestPrograms.NameOf(source)),
                delegate { return position < input.Length ? input[position++] : -1; },
                delegate(byte value)
                {
                    if (outcome.Output.Count >= refuseAfter)
                    {
                        throw new RefusedOutput();
                    }

                    outcome.Output.Add(value);
                },
                budget);
            try
            {
                if (preset.HasValue)
                {
                    machine.SetCell(0, preset.Value);
                }

                try
                {
                    outcome.Result = machine.RunRaw();
                    outcome.Fault = machine.Fault;
                }
                catch (RefusedOutput)
                {
                    outcome.Result = -1;
                    outcome.Fault = machine.Fault;
                }

                outcome.Iterations = machine.TotalIterations;
            }
            finally
            {
                machine.Dispose();
            }

            return outcome;
        }

        private sealed class RefusedOutput : Exception
        {
        }

        private static Outcome RunReference(string source, byte[] input, long budget)
        {
            var outcome = new Outcome();
            int position = 0;
            var machine = new BfVm(new BfProgram(source), delegate { return position < input.Length ? input[position++] : -1; }, delegate(byte value) { outcome.Output.Add(value); }, budget);
            try
            {
                outcome.Result = machine.Run() ? 1 : 0;
            }
            catch (BfFault fault)
            {
                outcome.Result = -1;
                outcome.Fault = fault.Message;
            }

            outcome.Iterations = machine.TotalIterations;
            return outcome;
        }

        private static void HostileProgram()
        {
            Outcome loop = RunCompiled("+[]", new byte[0], 100000);
            Assert.True(loop.Result == -1 && loop.Fault.Contains("loop iterations") && loop.Iterations <= 100001, "an endless loop ends at the iteration budget");
            Outcome clear = RunCompiled("+[-+]", new byte[0], 100000);
            Assert.True(clear.Result == -1 && clear.Fault.Contains("loop iterations"), "an endless loop that looks busy ends at the iteration budget");

            Assert.True(RunCompiled("<", new byte[0], 1000).Fault.Contains("outside its tape"), "moving left of the tape faults");
            Assert.Equal(1, RunCompiled(new string('>', TapeLength - 1), new byte[0], 1000).Result, "the last tape cell is reachable");
            Assert.True(RunCompiled(new string('>', TapeLength), new byte[0], 1000).Fault.Contains("outside its tape"), "moving right of the tape faults");
            Outcome walk = RunCompiled("+[>+]", new byte[0], 10000000);
            Assert.True(walk.Result == -1 && walk.Fault.Contains("outside its tape"), "a runaway walk faults at the tape's end");

            Assert.True(RunCompiled("-.", new byte[0], 1000).Fault.Contains("not a byte"), "a negative output faults");
            Assert.True(RunCompiled(new string('+', 256) + ".", new byte[0], 1000).Fault.Contains("not a byte"), "an output above 255 faults");

            Outcome flood = RunCompiled("+[.]", new byte[0], 100000);
            Assert.True(flood.Result == -1 && flood.Fault.Contains("loop iterations") && flood.Output.Count <= 100000, "an output flood without input ends at the iteration budget");
            Outcome refused = RunCompiled("+[.]", new byte[0], IterationBudget, 4096);
            Assert.True(refused.Result == -1 && refused.Fault.Contains("refused") && refused.Output.Count == 4096, "the host can refuse further output");

            Assert.Equal(0, RunCompiled(",[.,]", new byte[] { 1, 2, 3 }, 1000).Result, "input starvation ends the program as stopped");
            Assert.True(RunCompiled(",[.,]", new byte[] { 1, 2, 3 }, 1000).Output.SequenceEqual(new byte[] { 1, 2, 3 }), "input reaches output");

            Outcome overflow = RunCompiled("+", new byte[0], 1000, int.MaxValue, int.MaxValue);
            Assert.True(overflow.Result == -1 && overflow.Fault.Contains("overflowed"), "a cell overflow faults instead of wrapping");

            // Budget boundary: n plain iterations need a budget of n.
            Assert.Equal(1, RunCompiled(TestPrograms.BudgetStraight, new byte[0], 5).Result, "a budget equal to the iterations suffices");
            Assert.Equal(-1, RunCompiled(TestPrograms.BudgetStraight, new byte[0], 4).Result, "one iteration less faults");
            // Reads reset the budget: each stretch between reads gets the full budget.
            Assert.Equal(1, RunCompiled(TestPrograms.BudgetPerRead, new byte[] { 3, 3 }, 3).Result, "the budget applies between two reads");
            Assert.Equal(-1, RunCompiled(TestPrograms.BudgetPerRead, new byte[] { 3, 3 }, 2).Result, "and is not larger than given");

            // Fresh instances of one program give the same results.
            Outcome first = RunCompiled(TestPrograms.Counter, new byte[] { 7, 8, 9 }, 10000);
            Outcome second = RunCompiled(TestPrograms.Counter, new byte[] { 7, 8, 9 }, 10000);
            Assert.True(first.Output.SequenceEqual(second.Output) && first.Iterations == second.Iterations && first.Result == second.Result, "execution is deterministic across fresh instances");
            Assert.True(first.Output.SequenceEqual(new byte[] { 65, 7, 8, 9 }), "and correct");
        }

        // Random programs compiled ahead of time and on the reference interpreter
        // (tests/harness): both must agree on outcome, fault, output and iteration count.
        private static void CompiledDifferentialFuzz()
        {
            int faults = 0, finished = 0, stopped = 0;
            foreach (TestPrograms.FuzzCase item in TestPrograms.Fuzz())
            {
                const long budget = 20000;
                Outcome compiled = RunCompiled(item.Source, item.Input, budget);
                Outcome reference = RunReference(item.Source, item.Input, budget);
                string context = " for program " + item.Source;
                Assert.Equal(reference.Result, compiled.Result, "both end the same way" + context);
                Assert.True(reference.Fault == compiled.Fault, "both report the same fault ('" + reference.Fault + "', '" + compiled.Fault + "')" + context);
                Assert.True(reference.Output.SequenceEqual(compiled.Output), "both write the same bytes" + context);
                Assert.True(reference.Iterations == compiled.Iterations, "both count the same loop iterations" + context);
                faults += compiled.Result == -1 ? 1 : 0;
                finished += compiled.Result == 1 ? 1 : 0;
                stopped += compiled.Result == 0 ? 1 : 0;
            }

            Assert.True(faults > 100 && finished > 100 && stopped > 100, string.Format(CultureInfo.InvariantCulture, "the fuzzing covered every outcome ({0} faults, {1} finished, {2} stopped)", faults, finished, stopped));
        }

        // Every closed-form loop shape, compiled ahead of time, on a prepared tape:
        // the tape afterwards equals plain reference execution of the same loop.
        private static void CompiledClosedForms()
        {
            foreach (TestPrograms.ShapeCase shape in TestPrograms.Shapes())
            {
                var plain = new BfVm(new BfProgram(shape.Code, false), delegate { return -1; }, delegate { }, 100000000);
                for (int index = 0; index < shape.Cells.Length; index++)
                {
                    plain.SetCell(index, shape.Cells[index]);
                }

                plain.Run();
                var machine = new AotMachine(AotMachine.Program(TestPrograms.NameOf(shape.Code)), delegate { return -1; }, delegate { }, 100000000);
                try
                {
                    for (int index = 0; index < shape.Cells.Length; index++)
                    {
                        machine.SetCell(index, shape.Cells[index]);
                    }

                    Assert.Equal(1, machine.RunRaw(), "the compiled loop finishes: " + shape.Name);
                    for (int index = 0; index < shape.Cells.Length + 8; index++)
                    {
                        Assert.Equal(plain.Cell(index), machine.Cell(index), "compiled " + shape.Name + " cell " + index);
                    }
                }
                finally
                {
                    machine.Dispose();
                }
            }
        }

        // One compiled program run by instances on several threads (as the        // DirectInput driver runs one per bridge): each instance owns its tape.
        private static void CompiledConcurrency()
        {
            var failures = 0;
            var threads = new List<Thread>();
            for (int t = 0; t < 8; t++)
            {
                byte seed = (byte)(t + 1);
                var thread = new Thread(delegate()
                {
                    for (int run = 0; run < 200; run++)
                    {
                        Outcome outcome = RunCompiled(TestPrograms.Counter, new[] { seed, seed }, 10000);
                        if (!outcome.Output.SequenceEqual(new byte[] { 65, seed, seed }) || outcome.Result != 0)
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }

            Assert.Equal(0, failures, "concurrent instances of one program do not interfere");
        }

        // ------------------------------------------------------------------
        // Session watchdog
        // ------------------------------------------------------------------

        private static void SessionWatchdog()
        {
            // one CMD_LOG-shaped frame (the test's command callback decides)
            IntPtr program = AotMachine.Program(TestPrograms.NameOf(TestPrograms.EmitThenRead(Frame(0x81, 0, 0, new byte[] { 0, 0, 0 }))));
            var release = new ManualResetEvent(false);
            var failed = new ManualResetEvent(false);
            string failure = null;
            SessionCommand command = delegate { release.WaitOne(); return 0; };
            SessionFailure onFailure = delegate(string message) { failure = message; failed.Set(); };
            try
            {
                // A program blocked in a host call while input waits: stalled.
                IntPtr session = bft_session_create(program, command, onFailure, 100000);
                Assert.True(session != IntPtr.Zero, "the session starts");
                Assert.Equal(0, bft_session_stalled(session, 100), "no input, no stall");
                bft_session_post(session, 0x05, new byte[0], 0);
                Thread.Sleep(300);
                Assert.Equal(1, bft_session_stalled(session, 100), "input unread for longer than the limit is a stall");
                Assert.Equal(0, bft_session_stalled(session, 5000), "but not before the limit");
                using (var guard = new NativeGuard(100, delegate { return 0; }))
                {
                    Assert.Equal(0, guard.Evaluate(session, 0, 100, (long)GetTickCount64()), "a stall without a held force is left alone");
                    guard.Written("wheel", 8, Force(0x90));
                    Assert.Equal(2, guard.Evaluate(session, 0, 100, (long)GetTickCount64()), "a stall while a force is held expires the lease");
                }

                release.Set();
                bool recovered = false;
                for (int wait = 0; wait < 100 && !recovered; wait++)
                {
                    Thread.Sleep(10);
                    recovered = bft_session_stalled(session, 100) == 0;
                }

                Assert.True(recovered, "a program that reads again is no longer stalled");
                Assert.True(bft_session_end(session, 2000) != 0, "the session ends");

                // A program that falls more than 1 MiB behind fails closed.
                release.Reset();
                session = bft_session_create(program, command, onFailure, 100000);
                var big = new byte[4096];
                for (int i = 0; i < 300 && !failed.WaitOne(0); i++)
                {
                    bft_session_post(session, 0x05, big, big.Length);
                }

                Assert.True(failed.WaitOne(2000) && failure.Contains("behind"), "an unbounded backlog fails the session");
                Assert.Equal(1, bft_session_failed(session), "the session is marked failed");
                release.Set();
                Assert.True(bft_session_end(session, 2000) != 0, "the failed session ends");

                // The iteration budget inside a session.
                failed.Reset();
                session = bft_session_create(AotMachine.Program(TestPrograms.NameOf("+[]")), command, onFailure, 100000);
                Assert.True(failed.WaitOne(2000) && failure.Contains("loop iterations"), "a spinning program fails its session");
                Assert.True(bft_session_end(session, 2000) != 0, "the spinning session ends");
            }
            finally
            {
                release.Set();
                GC.KeepAlive(command);
                GC.KeepAlive(onFailure);
            }
        }

        private static byte[] Force(int value)
        {
            return new byte[] { 0x11, 0x08, (byte)value, 0x80, 0, 0, 0 };
        }

        // ------------------------------------------------------------------
        // g29testhost.dll
        // ------------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal delegate void LeaseExpired(string reason);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SessionCommand(int type, int length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate void SessionFailure(string message);

        private const string Library = "g29testhost.dll";

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [DllImport(Library)]
        private static extern IntPtr bft_lease_start(IntPtr guard, IntPtr session, int maxHoldMs, int stallMs, LeaseExpired expired);

        [DllImport(Library)]
        private static extern void bft_lease_stop(IntPtr lease);

        [DllImport(Library, CharSet = CharSet.Unicode)]
        private static extern int bft_registry_writable(string path);

        [DllImport(Library)]
        private static extern int bft_parse_frames(byte[] bytes, int length, int[] last);

        [DllImport(Library)]
        private static extern IntPtr bft_session_create(IntPtr program, SessionCommand command, SessionFailure failure, long iterationBudget);

        [DllImport(Library)]
        private static extern void bft_session_post(IntPtr session, int type, byte[] payload, int length);

        [DllImport(Library)]
        private static extern int bft_session_stalled(IntPtr session, int milliseconds);

        [DllImport(Library)]
        private static extern int bft_session_failed(IntPtr session);

        [DllImport(Library)]
        private static extern int bft_session_end(IntPtr session, int milliseconds);
    }
}
