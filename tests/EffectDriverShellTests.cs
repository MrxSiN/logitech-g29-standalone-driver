using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The native COM shell (src/bridge/directinput/driver.c) without DirectInput:
    // its DIEFFECT serialization from real unmanaged structures against the
    // frozen reader vectors (test build g29drivertest.dll), and the shipping
    // g29ffb64.dll / g29ffb32.dll loaded by a smoke client of each bitness,
    // with calls that never open a device.
    internal static class EffectDriverShellTests
    {
        private const uint Ok = 0;
        private const uint InvalidParam = 0x80070057;
        private const uint Generic = 0x80004005;
        private const uint Unsupported = 0x80004001;
        private const uint NotInitialized = 0x80070015;

        internal static void Run()
        {
            SerializationMatchesTheReaderVectors();
            CallsWithoutADevice();
        }

        private static void SerializationMatchesTheReaderVectors()
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

                var allocations = new List<IntPtr>();
                try
                {
                    IntPtr effect = Build(fields, allocations);
                    uint changed = uint.Parse(fields["changed"], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    EffectParameters baseParameters = ReferenceReplay.Parameters(fields["base"].Split('/'), 0);
                    var payload = new PayloadWriter().U8('G').U8('D');
                    payload.Bytes(EngineParityTests.ProgramEngine.Params(baseParameters).ToArray());
                    payload.Bytes(SerializeEffect(effect, changed));
                    sequence++;
                    harness.Post(new Frame(0x05, 0, sequence, payload.ToArray()));
                    IList<Frame> frames = harness.Run();
                    string actual = frames[0].Payload[0] == 3 ? "invalid" : ReaderParityTests.Describe(frames[0].Payload);
                    if (actual != expected)
                    {
                        throw new InvalidOperationException(string.Format("Shell DIEFFECT mismatch: '{0}' produced '{1}'.", line, actual));
                    }
                }
                finally
                {
                    foreach (IntPtr allocation in allocations)
                    {
                        Marshal.FreeHGlobal(allocation);
                    }
                }

                checkedLines++;
            }

            Assert.True(checkedLines > 150, "shell serialization replayed");
            Assert.True(SerializeEffect(IntPtr.Zero, 0x100)[4] == 0, "a null DIEFFECT is serialized as absent");
        }

        // A DIEFFECT in unmanaged memory as a game would pass it (the fixture was
        // generated in a 64-bit process; so is the test).
        private static IntPtr Build(IDictionary<string, string> fields, IList<IntPtr> allocations)
        {
            Assert.True(IntPtr.Size == 8, "the reader vectors are 64-bit");
            IntPtr effect = Allocate(80, allocations);
            Marshal.WriteInt32(effect, 0, int.Parse(fields["size"], CultureInfo.InvariantCulture));
            Marshal.WriteInt32(effect, 4, unchecked((int)uint.Parse(fields["flags"], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            Marshal.WriteInt32(effect, 8, unchecked((int)uint.Parse(fields["dur"], CultureInfo.InvariantCulture)));
            Marshal.WriteInt32(effect, 16, unchecked((int)uint.Parse(fields["gain"], CultureInfo.InvariantCulture)));
            Marshal.WriteInt32(effect, 28, int.Parse(fields["axes"], CultureInfo.InvariantCulture));
            if (fields["dir"] != "-")
            {
                IntPtr directions = Allocate(4, allocations);
                Marshal.WriteInt32(directions, int.Parse(fields["dir"], CultureInfo.InvariantCulture));
                Marshal.WriteIntPtr(effect, 40, directions);
            }

            if (fields["env"] != "-")
            {
                string[] values = fields["env"].Split(',');
                IntPtr envelope = Allocate(20, allocations);
                Marshal.WriteInt32(envelope, 0, 20);
                for (int index = 0; index < 4; index++)
                {
                    Marshal.WriteInt32(envelope, 4 + (4 * index), unchecked((int)uint.Parse(values[index], CultureInfo.InvariantCulture)));
                }

                Marshal.WriteIntPtr(effect, 48, envelope);
            }

            string typeSpecific = fields["ts"];
            int colon = typeSpecific.IndexOf(':');
            Marshal.WriteInt32(effect, 56, int.Parse(typeSpecific.Substring(0, colon), CultureInfo.InvariantCulture));
            if (typeSpecific.Substring(colon + 1) != "-")
            {
                string[] values = typeSpecific.Substring(colon + 1).Split(',');
                IntPtr data = Allocate(Math.Max(48, 4 * values.Length), allocations);
                for (int index = 0; index < values.Length; index++)
                {
                    Marshal.WriteInt32(data, 4 * index, int.Parse(values[index], CultureInfo.InvariantCulture));
                }

                Marshal.WriteIntPtr(effect, 64, data);
            }

            Marshal.WriteInt32(effect, 72, unchecked((int)uint.Parse(fields["delay"], CultureInfo.InvariantCulture)));
            return effect;
        }

        private static IntPtr Allocate(int size, IList<IntPtr> allocations)
        {
            IntPtr pointer = Marshal.AllocHGlobal(size);
            allocations.Add(pointer);
            for (int index = 0; index < size; index++)
            {
                Marshal.WriteByte(pointer, index, 0);
            }

            return pointer;
        }

        private static byte[] SerializeEffect(IntPtr effect, uint changed)
        {
            var buffer = new byte[4096];
            int length = G29TestSerializeEffect(effect, changed, buffer, buffer.Length);
            Assert.True(length > 0, "the DIEFFECT serialization fits a frame");
            var result = new byte[length];
            Array.Copy(buffer, result, length);
            return result;
        }

        [DllImport("g29drivertest.dll")]
        private static extern int G29TestSerializeEffect(IntPtr effect, uint changed, byte[] output, int capacity);

        // drivercheck64/32.exe load the shipping DLL of their bitness through its
        // class factory and check the calls that need no device.
        private static void CallsWithoutADevice()
        {
            string directory = System.IO.Path.GetDirectoryName(typeof(EffectDriverShellTests).Assembly.Location);
            foreach (string bits in new[] { "64", "32" })
            {
                var start = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(directory, "drivercheck" + bits + ".exe"), "g29ffb" + bits + ".dll")
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using (System.Diagnostics.Process check = System.Diagnostics.Process.Start(start))
                {
                    string output = check.StandardOutput.ReadToEnd().Trim();
                    check.WaitForExit();
                    Assert.True(check.ExitCode == 0 && output == "PASS " + bits + "-bit", "g29ffb" + bits + ".dll: " + output);
                }
            }
        }
    }
}
