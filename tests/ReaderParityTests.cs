using System;
using System.Collections.Generic;
using System.Globalization;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The Brainfuck DIEFFECT interpretation (role TEST 'G' 'D') against the frozen
    // legacy DirectInputEffectReader vectors (tests/reference/ffb-reader.txt). The
    // fixture was generated in a 64-bit process (DIEFFECT 80 bytes, DX5 72).
    internal static class ReaderParityTests
    {
        private const int Dx5Size = 72;
        private const int FullSize = 80;

        internal static void Run()
        {
            var harness = new BfHarness(BfMainTests.Program, BfVm.DefaultStepBudget);
            harness.Post(BfMainTests.Boot(4));
            harness.Run();
            ushort sequence = 0;
            int checkedLines = 0;
            foreach (string line in ReferenceFixtures.Lines("ffb-reader.txt"))
            {
                int arrow = line.IndexOf(" => ", StringComparison.Ordinal);
                string expected = line.Substring(arrow + 4);
                var fields = new Dictionary<string, string>();
                foreach (string part in line.Substring(0, arrow).Split(' '))
                {
                    int equals = part.IndexOf('=');
                    if (equals > 0)
                    {
                        fields[part.Substring(0, equals)] = part.Substring(equals + 1);
                    }
                }

                EffectParameters baseParameters = ReferenceReplay.Parameters(fields["base"].Split('/'), 0);
                var payload = new PayloadWriter().U8('G').U8('D');
                payload.Bytes(EngineParityTests.ProgramEngine.Params(baseParameters).ToArray());
                payload.Bytes(Serialize(fields));
                sequence++;
                harness.Post(new Frame(0x05, 0, sequence, payload.ToArray()));
                IList<Frame> frames = harness.Run();
                if (frames.Count != 1 || frames[0].Type != 0x8F)
                {
                    throw new InvalidOperationException("Unexpected DIEFFECT answer for: " + line);
                }

                string actual = frames[0].Payload[0] == 3 ? "invalid" : Describe(frames[0].Payload);
                if (actual != expected)
                {
                    throw new InvalidOperationException(string.Format("DIEFFECT vector mismatch: '{0}' produced '{1}'.", line, actual));
                }

                checkedLines++;
            }

            Assert.True(checkedLines > 150, "DIEFFECT fixture replayed");
        }

        // The DIEFFECT part of EV_DI_DOWNLOAD_EFFECT (dieffect.bfa), as the bridge
        // builds it from the structure in memory.
        internal static byte[] Serialize(IDictionary<string, string> fields)
        {
            uint size = uint.Parse(fields["size"], CultureInfo.InvariantCulture);
            var writer = new PayloadWriter()
                .U32(uint.Parse(fields["changed"], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .U8(1)
                .U32(size).U32(Dx5Size).U32(FullSize)
                .U32(uint.Parse(fields["flags"], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .U32(uint.Parse(fields["dur"], CultureInfo.InvariantCulture))
                .U32(0)
                .U32(uint.Parse(fields["gain"], CultureInfo.InvariantCulture))
                .U32(0).U32(0)
                .U32(uint.Parse(fields["axes"], CultureInfo.InvariantCulture));
            string direction = fields["dir"];
            writer.U8(direction == "-" ? 0 : 1).U32(direction == "-" ? 0 : unchecked((uint)int.Parse(direction, CultureInfo.InvariantCulture)));
            string envelope = fields["env"];
            if (envelope == "-")
            {
                writer.U8(0).U32(0).U32(0).U32(0).U32(0);
            }
            else
            {
                string[] values = envelope.Split(',');
                writer.U8(1);
                foreach (string value in values)
                {
                    writer.U32(uint.Parse(value, CultureInfo.InvariantCulture));
                }
            }

            // Only a full structure has a start delay; the bridge never reads past dwSize.
            writer.U32(size >= FullSize ? uint.Parse(fields["delay"], CultureInfo.InvariantCulture) : 0);
            string typeSpecific = fields["ts"];
            int colon = typeSpecific.IndexOf(':');
            uint typeSize = uint.Parse(typeSpecific.Substring(0, colon), CultureInfo.InvariantCulture);
            string values32 = typeSpecific.Substring(colon + 1);
            if (values32 == "-")
            {
                writer.U8(0).U32(typeSize).U16(0);
            }
            else
            {
                var bytes = new List<byte>();
                foreach (string value in values32.Split(','))
                {
                    bytes.AddRange(BitConverter.GetBytes(int.Parse(value, CultureInfo.InvariantCulture)));
                }

                int count = (int)Math.Min(Math.Min(typeSize, 24u), (uint)bytes.Count);
                writer.U8(1).U32(typeSize).U16(count).Bytes(bytes.GetRange(0, count).ToArray());
            }

            return writer.ToArray();
        }

        // The 13-field fixture form of the parameters the program answered.
        internal static string Describe(byte[] answer)
        {
            var reader = new PayloadReader(answer);
            reader.U8();
            int kind = reader.U8();
            long duration = reader.U32();
            long gain = reader.U32();
            long delay = reader.U32();
            int direction = reader.U8() != 0 ? -1 : 1;
            bool hasEnvelope = reader.U8() != 0;
            long attackLevel = reader.U32();
            long attackTime = reader.U32();
            long fadeLevel = reader.U32();
            long fadeTime = reader.U32();
            long magnitude = Signed(reader);
            long rampStart = Signed(reader);
            long rampEnd = Signed(reader);
            long offset = Signed(reader);
            long phase = reader.U32();
            long period = reader.U32();
            long conditionOffset = Signed(reader);
            long positive = Signed(reader);
            long negative = Signed(reader);
            long positiveSaturation = reader.U32();
            long negativeSaturation = reader.U32();
            long deadBand = Signed(reader);
            reader.End();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12},{13},{14},{15},{16},{17}",
                kind, duration, gain, delay, direction,
                hasEnvelope ? string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", attackLevel, attackTime, fadeLevel, fadeTime) : "-",
                magnitude, rampStart, rampEnd, offset, phase, period,
                conditionOffset, positive, negative, positiveSaturation, negativeSaturation, deadBand);
        }

        private static long Signed(PayloadReader reader)
        {
            bool negative = reader.U8() != 0;
            long value = reader.U32();
            return negative ? -value : value;
        }
    }
}
