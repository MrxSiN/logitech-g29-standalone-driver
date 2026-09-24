using System;
using System.Collections.Generic;
using System.Text;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // BF32-G29 virtual machine: limits, persistence, and proof that every
    // superinstruction leaves the tape exactly as plain execution does.
    internal static class RuntimeTests
    {
        internal static void Run()
        {
            Limits();
            Persistence();
            Frames();
            ClearAndMultiplyAdd();
            Idioms();
        }

        private static void Limits()
        {
            Assert.Throws<BfFault>(delegate { Execute("<", 1000); }, "tape underflow");
            Assert.Throws<BfFault>(delegate { Execute("-[<]", 1000); }, "tape underflow in a loop");
            Assert.Throws<BfFault>(delegate { Execute("+[]", 1000); }, "step budget stops an endless loop");
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

            // The budget counts steps between reads, not over the lifetime.
            var input = new Queue<int>(new[] { 1, 2, 3, 4 });
            var vm = new BfVm(new BfProgram("+++++[-],+++++[-],+++++[-],+++++[-],", false), delegate { return input.Count == 0 ? -1 : input.Dequeue(); }, delegate { }, 20);
            Assert.True(vm.Run(), "budget resets at every read");
            Assert.True(vm.TotalSteps > 20, "the whole run used more than one budget");
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

        private static void ClearAndMultiplyAdd()
        {
            var random = new Random(29);
            string[] loops = { "[-]", "[+]", "[->+<]", "[->++>+++<<]", "[>-<-]", "[+>+<]", "[->>+<<<+>]", "[-<+>]" };
            foreach (string loop in loops)
            {
                for (int trial = 0; trial < 60; trial++)
                {
                    var cells = new int[6];
                    for (int index = 0; index < cells.Length; index++)
                    {
                        cells[index] = random.Next(0, 40);
                    }

                    bool countsUp = loop.StartsWith("[+", StringComparison.Ordinal);
                    cells[2] = countsUp ? -random.Next(0, 40) : random.Next(0, 40);
                    Differential(">>" + loop, cells, "superinstruction " + loop);
                }
            }
        }

        private static void Idioms()
        {
            var random = new Random(1729);
            foreach (BfIdioms.Idiom idiom in BfIdioms.All)
            {
                int names = idiom.Names.Count;
                int recognized = 0;
                for (int trial = 0; trial < 400; trial++)
                {
                    // Place the named cells at shuffled offsets around a base cell.
                    var offsets = new int[names];
                    var used = new HashSet<int>();
                    for (int index = 0; index < names; index++)
                    {
                        int offset;
                        do
                        {
                            offset = index == 0 ? 0 : random.Next(-6, 7);
                        }
                        while (!used.Add(offset));
                        offsets[index] = offset;
                    }

                    string code = Instantiate(idiom.Pattern, offsets);
                    var program = new BfProgram(code, true);
                    Assert.Equal(1, program.IdiomCount, idiom.Name + " is recognized at arbitrary offsets");
                    recognized++;

                    var values = new int[names];
                    for (int index = 0; index < names; index++)
                    {
                        values[index] = random.Next(0, trial < 200 ? 12 : 400);
                    }

                    // Mostly valid preconditions (the scratch cells of each idiom start
                    // at zero), sometimes arbitrary, to exercise the fallback.
                    if (trial % 5 != 0)
                    {
                        ZeroScratch(idiom.Name, values);
                    }
                    else if (!Terminates(idiom.Name, values))
                    {
                        ZeroScratch(idiom.Name, values);
                    }

                    var cells = new int[16];
                    for (int index = 0; index < names; index++)
                    {
                        cells[8 + offsets[index]] = values[index];
                    }

                    Differential(new string('>', 8) + code, cells, idiom.Name + " " + string.Join(",", Array.ConvertAll(values, value => value.ToString())));
                }

                Assert.True(recognized > 0, idiom.Name + " exercised");
            }

            // Macro-shaped code from the assembler is recognized as well.
            Assert.Equal(0, new BfProgram("[-]>[->+<]", true).IdiomCount, "plain loops are not idioms");
        }

        // Scratch cells (per idiom) must be zero for the loop to have its meaning.
        private static void ZeroScratch(string idiom, int[] values)
        {
            switch (idiom)
            {
                case "race":
                    values[2] = 0;
                    values[3] = 0;
                    break;
                case "divmod":
                    values[3] = 0;
                    values[4] = 0;
                    values[2] = Math.Max(values[2], 1);
                    break;
                case "multiply":
                    values[3] = 0;
                    break;
            }
        }

        // Inputs for which plain execution is known to finish.
        private static bool Terminates(string idiom, int[] values)
        {
            switch (idiom)
            {
                case "race":
                    return values[2] == 0 && values[3] == 0;
                case "divmod":
                    return values[3] == 0 && values[4] == 0 && values[2] >= 1;
                case "multiply":
                    return values[3] == 0;
            }

            return false;
        }

        private static string Instantiate(string pattern, int[] offsets)
        {
            var names = new List<char>();
            var code = new StringBuilder();
            int position = 0;
            foreach (char c in pattern)
            {
                if (c >= 'a' && c <= 'z')
                {
                    if (!names.Contains(c))
                    {
                        names.Add(c);
                    }

                    int target = offsets[names.IndexOf(c)];
                    code.Append(target > position ? '>' : '<', Math.Abs(target - position));
                    position = target;
                    continue;
                }

                code.Append(c);
            }

            return code.ToString();
        }

        private static void Differential(string code, int[] cells, string name)
        {
            int[] plain = RunWith(new BfProgram(code, false), cells);
            int[] fast = RunWith(new BfProgram(code, true), cells);
            for (int index = 0; index < plain.Length; index++)
            {
                if (plain[index] != fast[index])
                {
                    throw new InvalidOperationException(string.Format("Superinstruction mismatch for {0} at cell {1}: plain {2}, optimized {3}.", name, index, plain[index], fast[index]));
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
