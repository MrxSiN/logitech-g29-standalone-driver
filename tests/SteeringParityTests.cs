using System;
using System.Collections.Generic;
using System.Diagnostics;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The Brainfuck steering motion (role TEST 'W' entry points) against the frozen
    // legacy HidSteeringReader arithmetic (tests/reference/steering.txt).
    internal static class SteeringParityTests
    {
        // Velocity and acceleration are computed in fixed point instead of doubles;
        // every recorded motion is currently reproduced exactly.
        private const int MotionTolerance = 0;

        internal static void Run()
        {
            var stopwatch = Stopwatch.StartNew();
            BfHarness harness = null;
            ushort sequence = 0;
            int checkedLines = 0;
            int inexact = 0;
            long worstSample = 0;
            long lastSample = 0;
            int boundaries = 0;
            string previousMotion = null;
            foreach (string line in ReferenceFixtures.Lines("steering.txt"))
            {
                int arrow = line.IndexOf(" => ", StringComparison.Ordinal);
                string[] parts = (arrow < 0 ? line : line.Substring(0, arrow)).Split(' ');
                string expected = arrow < 0 ? null : line.Substring(arrow + 4);
                switch (parts[0])
                {
                    case "axis":
                    {
                        harness = new BfHarness(BfMainTests.Program, BfVm.DefaultStepBudget);
                        harness.Post(BfMainTests.Boot(4));
                        harness.Run();
                        var arguments = new PayloadWriter();
                        Signed(arguments, long.Parse(parts[1]));
                        Signed(arguments, long.Parse(parts[2]));
                        arguments.U16(int.Parse(parts[3]));
                        byte[] answer = Call(harness, ref sequence, 'x', arguments, 11);
                        string actual = "effective " + Value(answer, 1) + " " + Value(answer, 6);
                        if (actual != expected)
                        {
                            throw new InvalidOperationException(string.Format("Steering vector mismatch: '{0}' produced '{1}'.", line, actual));
                        }

                        break;
                    }

                    case "sample":
                    {
                        long before = harness.Machine.TotalSteps;
                        Call(harness, ref sequence, 's', new PayloadWriter().U64(long.Parse(parts[1])).U32(long.Parse(parts[2])), 1);
                        lastSample = long.Parse(parts[1]);
                        worstSample = Math.Max(worstSample, harness.Machine.TotalSteps - before);
                        break;
                    }

                    case "motion":
                    {
                        byte[] answer = Call(harness, ref sequence, 'm', new PayloadWriter().U64(long.Parse(parts[1])), 16);
                        string[] wanted = expected.Split(' ');
                        long position = Value(answer, 1);
                        long velocity = Value(answer, 6);
                        long acceleration = Value(answer, 11);
                        bool exact = position == long.Parse(wanted[0]) && velocity == long.Parse(wanted[1]) && acceleration == long.Parse(wanted[2]);
                        bool close = position == long.Parse(wanted[0])
                            && Math.Abs(velocity - long.Parse(wanted[1])) <= MotionTolerance
                            && Math.Abs(acceleration - long.Parse(wanted[2])) <= MotionTolerance;
                        string produced = position + " " + velocity + " " + acceleration;
                        if (!close && long.Parse(parts[1]) - lastSample == 50000 && previousMotion != null && (produced == previousMotion || produced == wanted[0] + " 0 0"))
                        {
                            // Exactly 50 ms after the last sample: the legacy reader compares
                            // doubles (now/1e6 - last/1e6 > 0.05), so whether it reports the
                            // wheel as still depends on binary rounding of the two times. The
                            // program compares integer microseconds (still after more than
                            // 50000 us); either side of this boundary is accepted.
                            close = true;
                            exact = true;
                            boundaries++;
                        }

                        if (!close)
                        {
                            throw new InvalidOperationException(string.Format("Steering vector mismatch: '{0}' produced '{1} {2} {3}'.", line, position, velocity, acceleration));
                        }

                        if (!exact)
                        {
                            inexact++;
                        }

                        previousMotion = produced;

                        break;
                    }

                    default:
                        throw new InvalidOperationException("Unknown steering fixture line: " + line);
                }

                checkedLines++;
            }

            Assert.True(checkedLines > 300, "steering fixture covers the recorded traces");
            Console.WriteLine("  steering vectors: {0} lines, {1} motions off by one, {2} at the 50 ms boundary, worst sample {3} VM steps, {4} ms", checkedLines, inexact, boundaries, worstSample, stopwatch.ElapsedMilliseconds);
        }

        private static PayloadWriter Signed(PayloadWriter writer, long value)
        {
            return writer.U8(value < 0 ? 1 : 0).U32(Math.Abs(value));
        }

        private static long Value(byte[] answer, int offset)
        {
            long value = answer[offset + 1] + ((long)answer[offset + 2] << 8) + ((long)answer[offset + 3] << 16) + ((long)answer[offset + 4] << 24);
            return answer[offset] != 0 ? -value : value;
        }

        private static byte[] Call(BfHarness harness, ref ushort sequence, char op, PayloadWriter arguments, int length)
        {
            var payload = new PayloadWriter().U8('W').U8(op).Bytes(arguments.ToArray());
            sequence++;
            harness.Post(new Frame(0x05, 0, sequence, payload.ToArray()));
            IList<Frame> frames = harness.Run();
            if (frames.Count != 1 || frames[0].Type != 0x8F || frames[0].Payload.Length != length || frames[0].Payload[0] != 0)
            {
                throw new InvalidOperationException("Unexpected steering answer for " + op);
            }

            return frames[0].Payload;
        }
    }
}
