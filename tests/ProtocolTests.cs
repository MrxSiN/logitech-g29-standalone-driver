using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    internal static class ProtocolTests
    {
        internal static void Run()
        {
            string committed = File.ReadAllText(Path.Combine(BfMainTests.RepositoryRoot, @"src\brainfuck\g29-main.bf"));
            foreach (string binary in new[] { "g29ctl.exe", "g29ffb64.dll", "g29ffb32.dll" })
            {
                Assert.True(ReadEmbeddedProgram(binary) == committed, binary + " embeds the committed g29-main.bf");
            }

            var protocol = new ProgramProtocol();
            Assert.True(protocol.IsG29Hardware(0xC24F, 0), "native G29 identification");
            Assert.True(protocol.IsG29Hardware(0xC294, 0x1350), "compatibility-mode revision identification");
            Assert.True(protocol.IsG29Hardware(0xC29B, 0x8900), "alternate G29 revision identification");
            Assert.False(protocol.IsG29Hardware(0xC29B, 0x1230), "a real G27 must not be treated as a G29");
            Assert.True(protocol.IsPs4Mode(0xC260, 0x8900), "PS4-mode G29 identification");
            Assert.False(protocol.IsPs4Mode(0xC260, 0x1230), "unknown PS4-mode revision is not identified");
            Assert.False(protocol.IsG29Hardware(0xC260, 0x8900), "PS4-mode G29 is never configured");

            IList<byte[]> switchReports = protocol.SwitchToNative();
            Assert.Equal(2, switchReports.Count, "native switch report count");
            Assert.Bytes(new byte[] { 0xF8, 0x0A, 0, 0, 0, 0, 0 }, switchReports[0], "native switch reset report");
            Assert.Bytes(new byte[] { 0xF8, 0x09, 0x05, 0x01, 0x01, 0, 0 }, switchReports[1], "native switch detach report");

            Assert.Bytes(new byte[] { 0xF8, 0x81, 0x84, 0x03, 0, 0, 0 }, protocol.SetRange(900), "900-degree range report");
            Assert.Bytes(new byte[] { 0xF5, 0, 0, 0, 0, 0, 0 }, protocol.SetAutoCenter(0)[0], "autocenter off report");
            Assert.Equal(1, protocol.SetAutoCenter(0).Count, "autocenter off report count");
            Assert.Bytes(new byte[] { 0xFE, 0x0D, 0x05, 0x05, 0x7E, 0, 0 }, protocol.SetAutoCenter(66)[0], "autocenter lower segment report");
            Assert.Bytes(new byte[] { 0xFE, 0x0D, 0x06, 0x06, 0x81, 0, 0 }, protocol.SetAutoCenter(67)[0], "autocenter upper segment report");
            Assert.Bytes(new byte[] { 0x14, 0, 0, 0, 0, 0, 0 }, protocol.SetAutoCenter(67)[1], "autocenter enable report");
            Assert.Bytes(new byte[] { 0xF8, 0x12, 0x1F, 0, 0, 0, 0 }, protocol.SetLeds(31), "LED report");
            Assert.Bytes(new byte[] { 0x13, 0, 0, 0, 0, 0, 0 }, protocol.SetConstantForce(0), "zero force stops slot");
            Assert.Bytes(new byte[] { 0x11, 0x08, 0xFF, 0x80, 0, 0, 0 }, protocol.SetConstantForce(100), "maximum positive force report");
            Assert.Bytes(new byte[] { 0x11, 0x08, 0x60, 0x80, 0, 0, 0 }, protocol.SetConstantForce(-25), "diagnostic negative force report");
            Assert.True(protocol.IsNative(0xC24F), "native product identification");
            Assert.False(protocol.IsNative(0xC294), "compatibility product is not native");
            Assert.Throws<ArgumentOutOfRangeException>(delegate { protocol.SetRange(39); }, "range lower bound");
            Assert.Throws<ArgumentOutOfRangeException>(delegate { protocol.SetAutoCenter(101); }, "autocenter upper bound");
            Assert.Throws<ArgumentOutOfRangeException>(delegate { protocol.SetConstantForce(-101); }, "force lower bound");

            ProgramBounds();
        }

        // The program enforces the protocol bounds itself (status 3 = OUT_OF_RANGE),
        // independently of the C# adapter's checks.
        private static void ProgramBounds()
        {
            var harness = new BfHarness(BfMainTests.Program, BfVm.DefaultStepBudget);
            harness.Post(BfMainTests.Boot(4));
            harness.Run();
            ushort sequence = 100;
            foreach (byte[] request in new[]
            {
                new byte[] { (byte)'R', 39, 0 }, new byte[] { (byte)'R', 0x85, 0x03 }, new byte[] { (byte)'R', 0, 1 + 3 },
                new byte[] { (byte)'A', 101 }, new byte[] { (byte)'A', 255 }, new byte[] { (byte)'L', 32 },
                new byte[] { (byte)'F', 0, 101 }, new byte[] { (byte)'F', 2, 10 }, new byte[] { (byte)'F', 1, 255 }
            })
            {
                harness.Post(new Frame(0x05, 0, ++sequence, request));
                IList<Frame> answer = harness.Run();
                Assert.Equal(1, answer.Count, "one answer per request");
                BfMainTests.Expect(answer[0], 0x8F, sequence, new byte[] { 3 }, "out-of-range request " + ReferenceFixtures.Hex(request));
            }

            harness.Post(new Frame(0x05, 0, 7, new byte[] { (byte)'R', 40, 0 }));
            BfMainTests.Expect(harness.Run()[0], 0x8F, 7, new byte[] { 0, 0xF8, 0x81, 0x28, 0, 0, 0, 0 }, "lowest legal range");
            harness.Post(new Frame(0x05, 0, 8, new byte[] { (byte)'Z' }));
            BfMainTests.Expect(harness.Run()[0], 0x8F, 8, new byte[] { 17 }, "unknown test selector");
            harness.Post(new Frame(0x05, 0, 9, new byte[] { (byte)'R', 40 }));
            BfMainTests.Expect(harness.Run()[0], 0x8F, 9, new byte[] { 1 }, "a short request is refused");
        }

        // The G29_PROGRAM resource of a shipping binary (either bitness).
        private static string ReadEmbeddedProgram(string binary)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(ProtocolTests).Assembly.Location), binary);
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, LoadLibraryAsImageResource | LoadLibraryAsDataFile);
            Assert.True(module != IntPtr.Zero, "load " + binary + " as data");
            try
            {
                IntPtr resource = FindResource(module, "G29_PROGRAM", new IntPtr(10));
                Assert.True(resource != IntPtr.Zero, binary + " carries the program");
                int size = SizeofResource(module, resource);
                IntPtr data = LockResource(LoadResource(module, resource));
                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, size);
                return System.Text.Encoding.ASCII.GetString(bytes);
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private const uint LoadLibraryAsDataFile = 0x00000002;
        private const uint LoadLibraryAsImageResource = 0x00000020;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindResource(IntPtr module, string name, IntPtr type);

        [DllImport("kernel32.dll")]
        private static extern int SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr data);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
