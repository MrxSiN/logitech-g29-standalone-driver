using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // Repeatable benchmark of the Brainfuck policy execution path.
    //
    // Method: each workload is an existing scenario suite. It runs once on the
    // reference interpreter while every harness records the exact input bytes the
    // program consumed and the output bytes it produced. Each recorded session is
    // then replayed on a fresh instance of the compiled program (g29testhost.dll, bft_replay:
    // input and output are plain buffers, no managed callback per byte), and the
    // output must equal the recording byte for byte. The reported native time is
    // the median of several full passes over all sessions of the workload.
    internal static class Benchmark
    {
        private const int Passes = 7;

        internal static int Run()
        {
            var workloads = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("ffb-engine", EngineParityTests.Run),
                new KeyValuePair<string, Action>("directinput", DirectInputTests.Run),
                new KeyValuePair<string, Action>("steering", SteeringParityTests.Run),
                new KeyValuePair<string, Action>("cli", CliParityTests.Run),
                new KeyValuePair<string, Action>("protocol", delegate { ProtocolTests.Run(); ReferenceReplay.Run(); }),
                new KeyValuePair<string, Action>("monitor+watchdog", delegate { MonitorParityTests.Run(); WatchdogTests.Run(); SelectionParityTests.Run(); }),
            };

            Console.WriteLine("workload            sessions  events  in-bytes  out-bytes  native-ms(median)  us/event  reference-ms");
            foreach (KeyValuePair<string, Action> workload in workloads)
            {
                var harnesses = new List<BfHarness>();
                BfHarness.Created = harnesses.Add;
                try
                {
                    workload.Value();
                }
                finally
                {
                    BfHarness.Created = null;
                }

                List<Session> sessions = harnesses.Where(h => h.Consumed.Count > 0).Select(h => new Session(h.Consumed.ToArray(), h.Produced.ToArray())).ToList();
                long events = sessions.Sum(s => (long)s.Events);
                long inBytes = sessions.Sum(s => (long)s.Input.Length);
                long outBytes = sessions.Sum(s => (long)s.Output.Length);

                var times = new List<double>();
                for (int pass = 0; pass < Passes; pass++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    foreach (Session session in sessions)
                    {
                        session.ReplayNative();
                    }

                    times.Add(stopwatch.Elapsed.TotalMilliseconds);
                }

                times.Sort();
                double median = times[times.Count / 2];

                var reference = Stopwatch.StartNew();
                foreach (Session session in sessions)
                {
                    session.ReplayReference();
                }

                double referenceMs = reference.Elapsed.TotalMilliseconds;
                Console.WriteLine("{0,-18} {1,9} {2,7} {3,9} {4,10} {5,18:F1} {6,9:F2} {7,13:F1}", workload.Key, sessions.Count, events, inBytes, outBytes, median, median * 1000.0 / Math.Max(1, events), referenceMs);
            }

            return 0;
        }

        private sealed class Session
        {
            internal Session(byte[] input, byte[] output)
            {
                Input = input;
                Output = output;
                int position = 0;
                while (position + Frame.HeaderLength <= input.Length)
                {
                    position += Frame.HeaderLength + (input[position + 6] | (input[position + 7] << 8));
                    Events++;
                }
            }

            internal byte[] Input { get; private set; }

            internal byte[] Output { get; private set; }

            internal int Events { get; private set; }

            internal void ReplayNative()
            {
                var buffer = new byte[Output.Length + 65536];
                int written;
                int result = NativeReplay.Run(Input, buffer, out written);
                if (result < 0 || written != Output.Length || !buffer.Take(written).SequenceEqual(Output))
                {
                    throw new InvalidOperationException("The native replay diverged from the recording (result " + result + ", " + written + " of " + Output.Length + " bytes).");
                }
            }

            internal void ReplayReference()
            {
                int position = 0;
                var output = new List<byte>(Output.Length);
                var vm = new BfVm(BfMainTests.Program, delegate { return position < Input.Length ? Input[position++] : -1; }, output.Add, BfVm.DefaultIterationBudget);
                vm.Run();
                if (!output.SequenceEqual(Output))
                {
                    throw new InvalidOperationException("The reference replay diverged from the recording.");
                }
            }
        }

        private static class NativeReplay
        {
            internal static int Run(byte[] input, byte[] output, out int written)
            {
                return bft_replay(input, input.Length, output, output.Length, out written);
            }

            [DllImport("g29testhost.dll")]
            private static extern int bft_replay(byte[] input, int length, byte[] output, int capacity, out int written);
        }
    }
}
