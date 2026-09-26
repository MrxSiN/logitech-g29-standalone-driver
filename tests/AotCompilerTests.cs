using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G29.BfAot;

namespace G29.Tests
{
    // The ahead-of-time compiler (tools/BfAot): source validation, operation
    // folding, the generated code for each optimization, and the proofs it
    // reports for the real program. Behavior of the generated code is covered by
    // the differential tests (SafetyTests, Program.CompiledPass).
    internal static class AotCompilerTests
    {
        internal static void Run()
        {
            Validation();
            Folding();
            Generation();
            RealProgram();
        }

        private static void Rejects(string source, string expected, string name)
        {
            try
            {
                FlatProgram.Parse(Encoding.ASCII.GetBytes(source));
            }
            catch (BfSourceException exception)
            {
                Assert.True(exception.Message.Contains(expected), name + ": " + exception.Message);
                return;
            }

            throw new InvalidOperationException("Assertion failed: the compiler accepted " + name);
        }

        private static void Validation()
        {
            foreach (string unbalanced in new[] { "[", "]", "[[]", "[]]", "+[>]<]", "][" })
            {
                Rejects(unbalanced, "unmatched", "unbalanced brackets " + unbalanced);
            }

            Rejects("+a", "0x61 is not a Brainfuck command", "a letter");
            Rejects("+ +", "0x20 is not a Brainfuck command", "a space");
            Rejects("+\t", "0x09", "a tab");
            Rejects("+\n+#", "Line 2, column 2", "a comment character, located");
            Assert.Equal(5, FlatProgram.Parse(Encoding.ASCII.GetBytes("+\r\n>\n[-]\r\n")).Ops.Length, "CR and LF are accepted as line endings");

            var big = new byte[FlatProgram.MaxSourceBytes + 1];
            for (int index = 0; index < big.Length; index++)
            {
                big[index] = (byte)'>';
            }

            try
            {
                FlatProgram.Parse(big);
                throw new InvalidOperationException("Assertion failed: a program over the size limit was accepted");
            }
            catch (BfSourceException exception)
            {
                Assert.True(exception.Message.Contains("limit"), "a program over the size limit is refused");
            }
        }

        private static FlatProgram Flat(string source)
        {
            return FlatProgram.Parse(Encoding.ASCII.GetBytes(source));
        }

        private static void Folding()
        {
            FlatProgram run = Flat("++++++++++");
            Assert.True(run.Ops.Length == 1 && run.Ops[0] == FlatProgram.Add && run.Args[0] == 10, "ten + are one add of 10");
            run = Flat("----------");
            Assert.True(run.Ops.Length == 1 && run.Args[0] == -10, "ten - are one add of -10");
            run = Flat(">>>>>>");
            Assert.True(run.Ops.Length == 1 && run.Ops[0] == FlatProgram.Move && run.Args[0] == 6, "six > are one move");
            run = Flat("<<<<<<");
            Assert.True(run.Ops.Length == 1 && run.Args[0] == -6, "six < are one move");
            Assert.Equal(0, Flat("+-><").Ops.Length, "opposite runs cancel");
            run = Flat("+>-<+");
            Assert.Equal(5, run.Ops.Length, "runs on different cells stay separate");
        }

        // The C generated for a small program (function bodies only).
        private static string Generate(string source, out CompileStatistics stats)
        {
            stats = new CompileStatistics();
            List<Node> program = AotCompiler.Load(Encoding.ASCII.GetBytes(source), stats);
            var prototypes = new List<string>();
            var definitions = new List<string>();
            CEmitter.Emit(program, "unit", AotCompiler.DefaultThreshold, stats, prototypes, definitions);
            return string.Join("\n", definitions);
        }

        private static string Generate(string source)
        {
            CompileStatistics stats;
            return Generate(source, out stats);
        }

        private static void Expect(string source, string fragment, string name)
        {
            string code = Generate(source);
            Assert.True(code.Contains(fragment), name + ": expected '" + fragment + "' in\n" + code);
        }

        private static void ExpectNot(string source, string fragment, string name)
        {
            string code = Generate(source);
            Assert.False(code.Contains(fragment), name + ": did not expect '" + fragment + "' in\n" + code);
        }

        private static void Generation()
        {
            // Runs become one checked operation; the pointer position is folded
            // into a constant index while it is known.
            Expect("+++++", "BF_INC(t[0], 5);", "a run is one checked addition");
            Expect("-----", "BF_DEC(t[0], 5);", "a run is one checked subtraction");
            Expect(">>>>>>+", "BF_INC(t[6], 1);", "moves fold into the cell index");
            ExpectNot(">>>>>>+", "p +=", "no pointer arithmetic while the position is known");
            ExpectNot(">>>>>>+", "goto tape", "no tape check where the position is proven");

            // Clear loops.
            Expect("[-]", "if (t[0] < 0) goto ovf;", "[-] on a negative cell would count past the limit");
            Expect("[-]", "t[0] = 0;", "[-] is one store");
            Expect("[+]", "if (t[0] > 0) goto ovf;", "[+] on a positive cell would count past the limit");

            // Constant folding after a clear: no overflow check for a known value.
            Expect("[-]+++++", "t[0] = 5;", "a known value becomes a constant store");
            ExpectNot("[-]+++++", "BF_INC", "no overflow check on a known value");

            // Multiply-add loops.
            Expect(",[->+<]", "BF_MULADD(t[1], 1);", "transfer loop");
            Expect(",[->++<]", "BF_MULADD(t[1], 2);", "multiply loop");
            Expect(",[->+>+++<<]", "BF_MULADD(t[2], 3);", "two targets");
            Expect("[-]+++++[->++<]", "BF_INC(t[1], 10);", "a multiply-add with a known counter is one addition");

            // Dead loops: a loop whose cell is known to be zero is removed.
            CompileStatistics stats;
            string dead = Generate("[-][>+<-]", out stats);
            Assert.False(dead.Contains("while"), "a loop right after a clear is removed");
            Assert.True(stats.DeadLoops == 1, "and counted");
            string twice = Generate(",[.,][.]");
            Assert.Equal(1, twice.Split(new[] { "while" }, StringSplitOptions.None).Length - 1, "a loop right after a loop on the same cell is removed");

            // Loops that move the pointer by an unknown amount: relative addressing
            // with a tape check only at a new extreme.
            string scan = Generate("+[>]<<+", out stats);
            Assert.True(scan.Contains("p = 0;") && scan.Contains("while (t[p]) {") && scan.Contains("(unsigned)(p - 2) >= BF_TAPE_LENGTH"), "a scan switches to relative addressing:\n" + scan);
            Assert.True(stats.TapeChecks == 2, "one check inside the scan and one at the new lower extreme (" + stats.TapeChecks + ")");
            ExpectNot("+[>]<>+", "p - 1", "no check for a position already proven");

            // The iteration budget is charged once per loop iteration.
            Expect(",[.,]", "BF_ITERATION();", "every plain loop charges the budget");
            ExpectNot("[-]", "BF_ITERATION", "closed forms do not");

            // Idioms become calls with a plain fallback.
            Expect(new string('>', 8) + "[->[->+>+<<]>>[-<<+>>]<<<]", "bf_idiom_multiply(t, 8, 9, 10, 11)", "the multiply idiom");
            Assert.Equal(0, stats.StaticFaults, "no proven fault in the scan program");
            Generate("<", out stats);
            Assert.Equal(1, stats.StaticFaults, "a move below cell 0 at a known position is a proven fault");

            // The same source always produces the same code.
            Assert.True(Generate(",[->+<]+[>]") == Generate(",[->+<]+[>]"), "generation is deterministic");
        }

        private static void RealProgram()
        {
            byte[] source = File.ReadAllBytes(ArchitectureTests.ProgramPath);
            var first = new CompileStatistics();
            List<Node> tree = AotCompiler.Load(source, first);
            var prototypes = new List<string>();
            var definitions = new List<string>();
            CEmitter.Emit(tree, "g29_program_run", AotCompiler.DefaultThreshold, first, prototypes, definitions);
            Assert.Equal(0, first.StaticFaults, "the compiler proves no tape or overflow fault in g29-main.bf");
            Assert.True(first.FlatOperations * 10 < first.Commands, "runs fold into far fewer operations");
            Assert.True(first.Clears > 0 && first.MulAdds > 0, "clear and multiply-add loops are recognized");
            foreach (string idiom in new[] { "race", "divmod", "multiply" })
            {
                int count;
                Assert.True(first.Idioms.TryGetValue(idiom, out count) && count > 0, "the " + idiom + " idiom occurs in the program");
            }

            var second = new CompileStatistics();
            var prototypes2 = new List<string>();
            var definitions2 = new List<string>();
            CEmitter.Emit(AotCompiler.Load(source, second), "g29_program_run", AotCompiler.DefaultThreshold, second, prototypes2, definitions2);
            Assert.True(definitions.SequenceEqual(definitions2) && prototypes.SequenceEqual(prototypes2), "compiling the program twice gives identical C");
            Console.WriteLine("  compiler: {0} commands, {1} operations, {2} functions, {3} tape checks, {4} overflow checks, {5} constant stores", first.Commands, first.FlatOperations, first.Functions, first.TapeChecks, first.OverflowChecks, first.ConstantStores);
        }
    }
}
