using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace G29.Tests
{
    // Every Brainfuck program besides g29-main.bf that the tests run compiled
    // ahead of time: hostile programs for the containment tests, session
    // programs, closed-form loop shapes on prepared tapes, and a deterministic
    // random corpus for the differential fuzz test.
    // test.ps1 writes them with `G29.Tests.exe --write-programs <dir>`, and
    // native.ps1 compiles that directory with tools/BfAot into g29testhost.dll.
    // A program missing from the DLL fails the test that needs it.
    internal static class TestPrograms
    {
        internal const int TapeLength = 131072;
        internal const int FuzzCount = 3000;

        private static List<string> all;
        private static Dictionary<string, int> index;
        private static List<FuzzCase> fuzz;
        private static List<ShapeCase> shapes;

        internal sealed class FuzzCase
        {
            internal string Source;
            internal byte[] Input;
        }

        // Hostile and boundary programs (SafetyTests.HostileVm, SessionWatchdog).
        internal static IEnumerable<string> Fixed()
        {
            yield return "+[]";
            yield return "+[-+]";
            yield return "<";
            yield return new string('>', TapeLength - 1);
            yield return new string('>', TapeLength);
            yield return "+[>+]";
            yield return "-.";
            yield return new string('+', 256) + ".";
            yield return "+[.]";
            yield return ",[.,]";
            yield return "+";
            yield return BudgetStraight;
            yield return BudgetPerRead;
            yield return Counter;
            yield return EmitThenRead(SafetyTests.Frame(0x81, 0, 0, new byte[] { 0, 0, 0 }));
        }

        // Five plain loop iterations ([-] inside keeps it from being a multiply-add).
        internal const string BudgetStraight = "+++++[->+>[-]<<]";

        // Three plain iterations after each of two reads (input 3, 3).
        internal const string BudgetPerRead = ",[->+>[-]<<],[->+>[-]<<]";

        internal const string Counter = "++++++++[>++++++++<-]>+.,[.,]";

        // Brainfuck that writes the given bytes and then reads input forever.
        internal static string EmitThenRead(byte[] bytes)
        {
            var source = new StringBuilder();
            foreach (byte b in bytes)
            {
                source.Append("[-]").Append('+', b).Append('.');
            }

            return source.Append(">+[<,>]").ToString();
        }

        // Random programs (balanced brackets, pointer starting at 4) and inputs.
        internal static IList<FuzzCase> Fuzz()
        {
            if (fuzz != null)
            {
                return fuzz;
            }

            var random = new Random(1969);
            const string alphabet = "+-<>.,[]++--";
            fuzz = new List<FuzzCase>();
            for (int trial = 0; trial < FuzzCount; trial++)
            {
                var source = new StringBuilder(">>>>");
                int depth = 0, length = 1 + random.Next(120);
                for (int i = 0; i < length; i++)
                {
                    char c = alphabet[random.Next(alphabet.Length)];
                    if (c == ']' && depth == 0)
                    {
                        c = '+';
                    }

                    depth += c == '[' ? 1 : c == ']' ? -1 : 0;
                    source.Append(c);
                }

                source.Append(']', depth);
                var input = new byte[random.Next(12)];
                random.NextBytes(input);
                fuzz.Add(new FuzzCase { Source = source.ToString(), Input = input });
            }

            return fuzz;
        }

        // A loop shape the compiler and the reference interpreter run in closed
        // form, with the tape it starts from (RuntimeTests, SafetyTests).
        internal sealed class ShapeCase
        {
            internal string Code;
            internal int[] Cells;
            internal string Name;
            internal string Idiom;
        }

        internal static IList<ShapeCase> Shapes()
        {
            if (shapes != null)
            {
                return shapes;
            }

            shapes = new List<ShapeCase>();
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
                    shapes.Add(new ShapeCase { Code = ">>" + loop, Cells = cells, Name = "closed form " + loop });
                }
            }

            random = new Random(1729);
            foreach (G29.Bridge.Runtime.BfIdioms.Idiom idiom in G29.Bridge.Runtime.BfIdioms.All)
            {
                int names = idiom.Names.Count;
                for (int trial = 0; trial < 400; trial++)
                {
                    // the named cells at shuffled offsets around a base cell
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
                    var values = new int[names];
                    for (int index = 0; index < names; index++)
                    {
                        values[index] = random.Next(0, trial < 200 ? 12 : 400);
                    }

                    // Mostly valid preconditions (the scratch cells of each idiom start
                    // at zero), sometimes arbitrary, to exercise the fallback.
                    if (trial % 5 != 0 || !Terminates(idiom.Name, values))
                    {
                        ZeroScratch(idiom.Name, values);
                    }

                    var cells = new int[16];
                    for (int index = 0; index < names; index++)
                    {
                        cells[8 + offsets[index]] = values[index];
                    }

                    shapes.Add(new ShapeCase { Code = new string('>', 8) + code, Cells = cells, Idiom = idiom.Name, Name = idiom.Name + " " + string.Join(",", Array.ConvertAll(values, value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))) });
                }
            }

            return shapes;
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

        // The corpus name of a program (its file name without .bf).
        internal static string NameOf(string source)
        {
            Build();
            int position;
            if (!index.TryGetValue(source, out position))
            {
                throw new InvalidOperationException("The program is not in the test corpus (TestPrograms): " + (source.Length > 60 ? source.Substring(0, 60) + "..." : source));
            }

            return "bfp_p" + position.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Write(string directory)
        {
            Build();
            Directory.CreateDirectory(directory);
            foreach (string stale in Directory.GetFiles(directory, "*.bf"))
            {
                File.Delete(stale);
            }

            for (int position = 0; position < all.Count; position++)
            {
                File.WriteAllText(Path.Combine(directory, "p" + position.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".bf"), all[position], Encoding.ASCII);
            }
        }

        private static void Build()
        {
            if (all != null)
            {
                return;
            }

            all = new List<string>();
            index = new Dictionary<string, int>(StringComparer.Ordinal);
            var sources = new List<string>(Fixed());
            foreach (FuzzCase item in Fuzz())
            {
                sources.Add(item.Source);
            }

            foreach (ShapeCase item in Shapes())
            {
                sources.Add(item.Code);
            }

            foreach (string source in sources)
            {
                if (!index.ContainsKey(source))
                {
                    index[source] = all.Count;
                    all.Add(source);
                }
            }
        }
    }
}
