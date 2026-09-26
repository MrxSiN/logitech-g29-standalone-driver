using System;
using System.Collections.Generic;
using System.Text;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The reference interpreter (tests/harness): limits, persistence, and proof that
    // every closed-form loop leaves the tape exactly as plain execution does.
    internal static class RuntimeTests
    {
        internal static void Run()
        {
            Limits();
            Persistence();
            Frames();
            ClosedForms();
        }

        private static void Limits()
        {
            Assert.Throws<BfFault>(delegate { Execute("<", 1000); }, "tape underflow");
            Assert.Throws<BfFault>(delegate { Execute("-[<]", 1000); }, "tape underflow in a loop");
            Assert.Throws<BfFault>(delegate { Execute("+[]", 1000); }, "the iteration budget stops an endless loop");
            Assert.Throws<BfFault>(delegate { Execute("-.", 1000); }, "non-byte output");
            Assert.Throws<BfFault>(delegate { Execute("-[-]", 1000); }, "clearing a negative cell overflows");
            Assert.Throws<BfFault>(delegate { Execute("-[->+<]", 1000); }, "counting a negative cell down overflows");
            Assert.Throws<FormatException>(delegate { new BfProgram("["); }, "unmatched [");
            Assert.Throws<FormatException>(delegate { new BfProgram("]"); }, "unmatched ]");

            var big = new StringBuilder();
            big.Append('>', BfVm.TapeLength - 1);
            Execute(big.ToString(), BfVm.TapeLength * 2);
            big.Append('>');
            Assert.Throws<BfFault>(delegate { Execute(big.ToString(), BfVm.TapeLength * 2); }, "tape end");

            // The budget counts loop iterations between reads, not over the lifetime.
            var input = new Queue<int>(new[] { 1, 2, 3, 4 });
            var vm = new BfVm(new BfProgram("+++++[-],+++++[-],+++++[-],+++++[-],", false), delegate { return input.Count == 0 ? -1 : input.Dequeue(); }, delegate { }, 8);
            Assert.True(vm.Run(), "budget resets at every read");
            Assert.True(vm.TotalIterations == 5 + 6 + 7 + 8, "the whole run used more than one budget");
        }

        private static void Persistence()
        {
            // A program that counts the bytes it has read and echoes the running count.
            var harness = new BfHarness(new BfProgram("+[>,<+>[-]<[->>+<<]>>[-<<+>>]<<.]"), 1000);
            var results = new List<byte>();
            harness.PostBytes(new byte[] { 9, 9, 9 });
            int produced = 0;
            try
            {
                harness.Run();
            }
            catch (AbiViolation)
            {
                produced = 1;
            }

            Assert.Equal(1, produced, "raw output that is not a frame is rejected");
        }

        private static void Frames()
        {
            var parser = new FrameParser();
            Assert.Throws<AbiViolation>(delegate { parser.Push(0x00); }, "bad magic");
            parser = new FrameParser();
            parser.Push(0xA5);
            Assert.Throws<AbiViolation>(delegate { parser.Push(2); }, "bad version");
            parser = new FrameParser();
            foreach (byte b in new byte[] { 0xA5, 1, 0x80, 0, 0, 0, 0x01 })
            {
                parser.Push(b);
            }

            Assert.Throws<AbiViolation>(delegate { parser.Push(0x10); }, "oversized payload");

            byte[] encoded = new Frame(0x81, 2, 0x1234, new byte[] { 1, 2, 3 }).Encode();
            Assert.Bytes(new byte[] { 0xA5, 1, 0x81, 2, 0x34, 0x12, 3, 0, 1, 2, 3 }, encoded, "frame encoding");
            parser = new FrameParser();
            Frame decoded = null;
            foreach (byte b in encoded)
            {
                decoded = parser.Push(b) ?? decoded;
            }

            Assert.True(decoded != null && decoded.Type == 0x81 && decoded.Flags == 2 && decoded.Sequence == 0x1234 && decoded.Payload.Length == 3, "frame round trip");

            var reader = new PayloadReader(new PayloadWriter().U8(7).U16(0x1234).U32(0x89ABCDEF).Text("hé").ToArray());
            Assert.Equal(7, reader.U8(), "u8");
            Assert.Equal(0x1234, reader.U16(), "u16");
            Assert.True(reader.U32() == 0x89ABCDEF, "u32");
            Assert.True(reader.Text() == "hé", "utf-8 text");
            reader.End();
            Assert.Throws<AbiViolation>(delegate { new PayloadReader(new byte[] { 1 }).U16(); }, "short payload");
            Assert.Throws<AbiViolation>(delegate { new PayloadReader(new byte[] { 1 }).End(); }, "trailing payload");
        }

        // Every closed form against plain execution of the same loop.
        private static void ClosedForms()
        {
            var recognized = new Dictionary<string, int>();
            foreach (TestPrograms.ShapeCase shape in TestPrograms.Shapes())
            {
                if (shape.Idiom != null)
                {
                    Assert.Equal(1, new BfProgram(shape.Code, true).IdiomCount, shape.Idiom + " is recognized at arbitrary offsets");
                    int count;
                    recognized.TryGetValue(shape.Idiom, out count);
                    recognized[shape.Idiom] = count + 1;
                }

                Differential(shape.Code, shape.Cells, shape.Name);
            }

            foreach (BfIdioms.Idiom idiom in BfIdioms.All)
            {
                Assert.True(recognized.ContainsKey(idiom.Name), idiom.Name + " exercised");
            }

            Assert.Equal(0, new BfProgram("[-]>[->+<]", true).IdiomCount, "plain loops are not idioms");
        }

        private static void Differential(string code, int[] cells, string name)
        {
            int[] plain = RunWith(new BfProgram(code, false), cells);
            int[] fast = RunWith(new BfProgram(code, true), cells);
            for (int index = 0; index < plain.Length; index++)
            {
                if (plain[index] != fast[index])
                {
                    throw new InvalidOperationException(string.Format("Closed-form mismatch for {0} at cell {1}: plain {2}, closed form {3}.", name, index, plain[index], fast[index]));
                }
            }
        }

        private static int[] RunWith(BfProgram program, int[] cells)
        {
            var vm = new BfVm(program, delegate { return -1; }, delegate { }, 100000000);
            for (int index = 0; index < cells.Length; index++)
            {
                vm.SetCell(index, cells[index]);
            }

            vm.Run();
            var result = new int[cells.Length + 8];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = vm.Cell(index);
            }

            return result;
        }

        private static void Execute(string code, long budget)
        {
            new BfVm(new BfProgram(code), delegate { return -1; }, delegate { }, budget).Run();
        }
    }
}
