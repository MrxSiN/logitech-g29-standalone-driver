using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The persistent program (src/brainfuck/g29-main.bf) driven through the framed
    // ABI by a fake bridge.
    internal static class BfMainTests
    {
        private static BfProgram program;

        internal static string RepositoryRoot
        {
            get { return Path.GetDirectoryName(Path.GetDirectoryName(ReferenceFixtures.Directory)); }
        }

        internal static BfProgram Program
        {
            get
            {
                if (program == null)
                {
                    program = new BfProgram(File.ReadAllText(Path.Combine(RepositoryRoot, @"src\brainfuck\g29-main.bf")));
                }

                return program;
            }
        }

        internal static void Run()
        {
            AssembledProgramIsCurrent();
            TestRole();
            MalformedFrames();
            OtherRoles();
        }

        private static void AssembledProgramIsCurrent()
        {
            var assembler = new BfAsm.Assembler();
            assembler.AssembleFile(Path.Combine(RepositoryRoot, @"src\brainfuck\g29-main.bfa"));
            string committed = File.ReadAllText(Path.Combine(RepositoryRoot, @"src\brainfuck\g29-main.bf"));
            Assert.True(assembler.Output == committed, "g29-main.bf matches its assembly source; run bf.ps1");
            IDictionary<string, int> idioms = Program.IdiomsByName();
            Assert.True(idioms.ContainsKey("race") && idioms.ContainsKey("divmod"), "the VM recognizes the library loops in the real program");
            Assert.True(Program.MulAddCount > 0, "multiply-add loops are recognized");

            AssemblerRejects("@0 [ > ]", "an unbalanced loop");
            AssemblerRejects("@0 [! > ] @0", "moving by name after an unbalanced loop");
            AssemblerRejects("@0 <", "moving below cell 0");
            AssemblerRejects("macro m(a) { @a } m(1, 2)", "wrong argument count");
            AssemblerRejects("local t { }", "locals without a scratch pool");
            AssemblerRejects("macro m() { m() } m()", "recursive macros");
            Assert.True(Assemble("scratch 10 to 12\nmacro z(x) { @x [-] }\nlocal a, b { z(b) } @0 [! > ] assume @3 +") == "\n>>>>>>>>>>>[-]<<<<<<<<<<<[>]+\n".TrimStart('\n'), "assembler output");
            foreach (char c in committed)
            {
                if ("+-<>[].,".IndexOf(c) < 0 && (c == '\0'))
                {
                    throw new InvalidOperationException("Unexpected character in g29-main.bf");
                }
            }
        }

        private static string Assemble(string source)
        {
            string path = Path.Combine(Path.GetTempPath(), "g29-bfasm-test-" + Guid.NewGuid().ToString("N") + ".bfa");
            File.WriteAllText(path, source);
            try
            {
                var assembler = new BfAsm.Assembler();
                assembler.AssembleFile(path);
                return assembler.Output;
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssemblerRejects(string source, string name)
        {
            try
            {
                Assemble(source);
            }
            catch (BfAsm.AsmException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: the assembler accepted " + name);
        }

        internal static Frame Boot(int role, params string[] arguments)
        {
            var payload = new PayloadWriter().U8(role).U8(8).U8(9).U8(0).U16(arguments.Length);
            foreach (string argument in arguments)
            {
                payload.Text(argument);
            }

            return new Frame(0x01, 0, 0, payload.ToArray());
        }

        private static void TestRole()
        {
            var harness = new BfHarness(Program, BfVm.DefaultStepBudget);
            harness.Post(Boot(4, "ignored", "arguments"));
            IList<Frame> boot = harness.Run();
            Assert.Equal(1, boot.Count, "boot answers once");
            Expect(boot[0], 0x8F, 0, Encoding.ASCII.GetBytes("BOOT"), "boot answer");

            harness.Post(new Frame(0x05, 0, 7, new byte[] { (byte)'E', 1, 2, 3 }));
            IList<Frame> first = harness.Run();
            Expect(first[0], 0x8F, 7, new byte[] { 1, 0, 0, 0, 1, 2, 3 }, "first test event");

            var large = new byte[301];
            for (int index = 0; index < large.Length; index++)
            {
                large[index] = index == 0 ? (byte)'E' : (byte)(index * 7);
            }

            harness.Post(new Frame(0x05, 0, 0xBEEF, large));
            harness.Post(new Frame(0x05, 0, 9, new byte[] { (byte)'E' }));
            IList<Frame> next = harness.Run();
            Assert.Equal(2, next.Count, "queued events are all handled");
            var expected = new List<byte> { 2, 0, 0, 0 };
            expected.AddRange(large); expected.RemoveAt(4);
            Expect(next[0], 0x8F, 0xBEEF, expected.ToArray(), "state persists across events; 300-byte payload");
            Expect(next[1], 0x8F, 9, new byte[] { 3, 0, 0, 0 }, "empty payload");

            harness.Post(new Frame(0x77, 0, 5, new byte[] { 1, 2 }));
            harness.Post(new Frame(0x01, 0, 6, Boot(4).Payload));
            harness.Post(new Frame(0x05, 0, 8, new byte[] { (byte)'E', 42 }));
            IList<Frame> errors = harness.Run();
            Assert.Equal(3, errors.Count, "unknown events are answered and skipped");
            Expect(errors[0], 0x84, 5, new byte[] { 17, 0x77 }, "unknown event type");
            Expect(errors[1], 0x84, 6, new byte[] { 17, 0x01 }, "second boot is not accepted");
            Expect(errors[2], 0x8F, 8, new byte[] { 4, 0, 0, 0, 42 }, "program continues after errors");

            harness.Post(new Frame(0x03, 0, 0, new byte[0]));
            IList<Frame> shutdown = harness.Run();
            Expect(shutdown[0], 0x80, 0, new byte[] { 0, 0, 0, 0 }, "shutdown exits with status 0");
            Assert.True(harness.Ended, "the program ends after CMD_EXIT");

            var early = new BfHarness(Program, BfVm.DefaultStepBudget);
            early.Post(new Frame(0x05, 0, 3, new byte[] { 1 }));
            early.Post(Boot(4));
            IList<Frame> beforeBoot = early.Run();
            Expect(beforeBoot[0], 0x84, 3, new byte[] { 18, 0x05 }, "events before boot are refused");
            Expect(beforeBoot[1], 0x8F, 0, Encoding.ASCII.GetBytes("BOOT"), "boot still works afterwards");
        }

        private static void MalformedFrames()
        {
            foreach (byte[] bad in new[]
            {
                new byte[] { 0x5A, 1, 0x05, 0, 0, 0, 0, 0 },
                new byte[] { 0xA5, 2, 0x05, 0, 0, 0, 0, 0 },
                new byte[] { 0xA5, 1, 0x05, 0, 0, 0, 0x01, 0x10 }
            })
            {
                var harness = new BfHarness(Program, BfVm.DefaultStepBudget);
                harness.Post(Boot(4));
                harness.Run();
                harness.PostBytes(bad);
                IList<Frame> frames = harness.Run();
                Assert.Equal(2, frames.Count, "a malformed frame is fatal");
                Assert.Equal(0x84, frames[0].Type, "malformed frame error");
                Assert.Equal(1, frames[0].Payload[0], "BAD_ABI_FRAME");
                Expect(frames[1], 0x80, 0, new byte[] { 70, 0, 0, 0 }, "malformed frame exit status");
                Assert.True(harness.Ended, "program stops after a malformed frame");
            }
        }

        private static void OtherRoles()
        {
            var harness = new BfHarness(Program, BfVm.DefaultStepBudget);
            harness.Post(Boot(2));
            IList<Frame> frames = harness.Run();
            Assert.Equal(0x81, frames[0].Type, "unmigrated role is logged");
            Expect(frames[1], 0x80, 0, new byte[] { 69, 0, 0, 0 }, "unmigrated role exits");
        }

        internal static void Expect(Frame frame, int type, int sequence, byte[] payload, string name)
        {
            Assert.Equal(type, frame.Type, name + " type");
            Assert.Equal(sequence, frame.Sequence, name + " sequence");
            Assert.Bytes(payload, frame.Payload, name + " payload");
        }
    }
}
