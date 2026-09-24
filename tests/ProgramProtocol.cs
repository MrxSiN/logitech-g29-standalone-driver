using System;
using System.Collections.Generic;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // Test adapter over the program's protocol entry points (role TEST, ABI.md):
    // identification and every report, with the host-side bounds and force
    // checks the legacy adapter applied.
    internal sealed class ProgramProtocol
    {
        public const string ProgramResourceName = BrainfuckProgram.ResourceName;

        private const int ReportLength = 7;
        private const byte NativeFlag = 0x01;
        private const byte G29HardwareFlag = 0x02;
        private const byte Ps4ModeFlag = 0x04;
        private const byte TestEvent = 0x05;
        private const byte TestResult = 0x8F;

        private readonly object sync = new object();
        private readonly BfHarness harness;
        private ushort sequence;

        public ProgramProtocol()
        {
            harness = new BfHarness(Program, BfVm.DefaultStepBudget);
            harness.Post(new Frame(0x01, 0, 0, new PayloadWriter().U8(4).U8(IntPtr.Size).U8(0).U8(0).U16(0).ToArray()));
            IList<Frame> boot = harness.Run();
            if (boot.Count != 1 || boot[0].Type != TestResult || boot[0].Payload.Length != 4)
            {
                throw new InvalidOperationException("The Brainfuck program did not start.");
            }
        }

        public static BfProgram Program
        {
            get { return BrainfuckProgram.Shared; }
        }

        public bool IsNative(ushort productId)
        {
            return (Identify(productId, 0) & NativeFlag) != 0;
        }

        public bool IsG29Hardware(ushort productId, ushort versionNumber)
        {
            return (Identify(productId, versionNumber) & G29HardwareFlag) != 0;
        }

        public bool IsPs4Mode(ushort productId, ushort versionNumber)
        {
            return (Identify(productId, versionNumber) & Ps4ModeFlag) != 0;
        }

        public IList<byte[]> SwitchToNative()
        {
            return Reports(Call('N'), 2);
        }

        public byte[] SetRange(int degrees)
        {
            RequireRange(degrees, 40, 900, "degrees");
            return Reports(Call('R', (byte)(degrees & 0xFF), (byte)((degrees >> 8) & 0xFF)), 1)[0];
        }

        public IList<byte[]> SetAutoCenter(int percent)
        {
            RequireRange(percent, 0, 100, "percent");
            return Reports(Call('A', (byte)percent), percent == 0 ? 1 : 2);
        }

        public byte[] SetLeds(int mask)
        {
            RequireRange(mask, 0, 31, "mask");
            return Reports(Call('L', (byte)mask), 1)[0];
        }

        public byte[] SetConstantForce(int percent)
        {
            RequireRange(percent, -100, 100, "percent");
            byte[] report = Reports(Call('F', (byte)(percent < 0 ? 1 : 0), (byte)Math.Abs(percent)), 1)[0];
            RequireForceWithinRequest(report, percent);
            return report;
        }

        public byte[] StopForce()
        {
            return Reports(Call('S'), 1)[0];
        }

        private byte Identify(ushort productId, ushort versionNumber)
        {
            byte[] output = Call('I', (byte)productId, (byte)(productId >> 8), (byte)versionNumber, (byte)(versionNumber >> 8));
            if (output.Length != 1 || (output[0] & ~(NativeFlag | G29HardwareFlag | Ps4ModeFlag)) != 0)
            {
                throw new InvalidOperationException("The Brainfuck program returned an invalid identification result.");
            }

            return output[0];
        }

        // Runs one role-TEST entry point and returns its data (after the status byte).
        private byte[] Call(char selector, params byte[] arguments)
        {
            var payload = new byte[arguments.Length + 1];
            payload[0] = (byte)selector;
            Buffer.BlockCopy(arguments, 0, payload, 1, arguments.Length);
            lock (sync)
            {
                sequence = (ushort)(sequence == ushort.MaxValue ? 1 : sequence + 1);
                harness.Post(new Frame(TestEvent, 0, sequence, payload));
                IList<Frame> frames = harness.Run();
                if (frames.Count != 1 || frames[0].Type != TestResult || frames[0].Sequence != sequence || frames[0].Payload.Length < 1)
                {
                    throw new InvalidOperationException("The Brainfuck program gave an unexpected answer.");
                }

                byte[] result = frames[0].Payload;
                if (result[0] != 0)
                {
                    throw new InvalidOperationException(string.Format("The Brainfuck program refused the request (error {0}).", result[0]));
                }

                var data = new byte[result.Length - 1];
                Buffer.BlockCopy(result, 1, data, 0, data.Length);
                return data;
            }
        }

        private static IList<byte[]> Reports(byte[] output, int expectedCount)
        {
            if (output.Length != expectedCount * ReportLength)
            {
                throw new InvalidOperationException(string.Format("The Brainfuck protocol produced {0} bytes; expected {1} report(s) of {2} bytes.", output.Length, expectedCount, ReportLength));
            }

            var reports = new List<byte[]>();
            for (int offset = 0; offset < output.Length; offset += ReportLength)
            {
                var report = new byte[ReportLength];
                Buffer.BlockCopy(output, offset, report, 0, ReportLength);
                reports.Add(report);
            }

            return reports;
        }

        // Safety net independent of the Brainfuck: a force report must be the stop
        // report for zero, and otherwise point the same way as the request with a
        // magnitude no larger than the request rounded up.
        private static void RequireForceWithinRequest(byte[] report, int percent)
        {
            bool valid;
            if (percent == 0)
            {
                valid = report[0] == 0x13 && report[1] == 0 && report[2] == 0 && report[3] == 0;
            }
            else
            {
                int offset = report[2] - 0x80;
                int limit = ((Math.Abs(percent) * 127) + 99) / 100;
                valid = report[0] == 0x11 && report[1] == 0x08 && report[3] == 0x80 &&
                    Math.Sign(offset) == Math.Sign(percent) && Math.Abs(offset) <= limit;
            }

            if (!valid || report[4] != 0 || report[5] != 0 || report[6] != 0)
            {
                throw new InvalidOperationException("The Brainfuck protocol produced a force report outside the requested bounds. No force was sent.");
            }
        }

        private static void RequireRange(int value, int minimum, int maximum, string parameterName)
        {
            if (value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, string.Format("Value must be between {0} and {1}.", minimum, maximum));
            }
        }
    }
}
