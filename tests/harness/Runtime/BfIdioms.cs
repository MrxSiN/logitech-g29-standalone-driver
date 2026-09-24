using System;
using System.Collections.Generic;

namespace G29.Bridge.Runtime
{
    // Closed-form execution of canonical Brainfuck loops.
    //
    // An idiom is written as Brainfuck in which a lower-case letter means "move to
    // that cell" (for example "a[-b+a]"). A compiled loop matches when it performs
    // exactly those commands on consistently placed cells. Every idiom has a
    // closed-form Execute that must leave the tape exactly as plain execution of
    // the whole loop would; when its preconditions do not hold it returns false and
    // the VM runs the loop plainly. RuntimeTests checks each idiom against plain
    // execution on generated inputs.
    internal static class BfIdioms
    {
        private static readonly List<Idiom> Known = new List<Idiom>();

        static BfIdioms()
        {
            BfIdiomLibrary.RegisterAll();
        }

        internal static void Register(Idiom idiom)
        {
            Known.Add(idiom);
        }

        internal static IList<Idiom> All
        {
            get { return Known; }
        }

        internal static Match Recognize(byte[] operations, int[] arguments, int open, int close)
        {
            foreach (Idiom idiom in Known)
            {
                int[] offsets = idiom.Bind(operations, arguments, open, close);
                if (offsets != null)
                {
                    return new Match(idiom, offsets);
                }
            }

            return null;
        }

        internal static bool Execute(Match match, int[] cells, int p)
        {
            int[] offsets = match.Offsets;
            var values = new long[offsets.Length];
            for (int index = 0; index < offsets.Length; index++)
            {
                int target = p + offsets[index];
                if ((uint)target >= BfVm.TapeLength)
                {
                    return false;
                }

                values[index] = cells[target];
            }

            if (!match.Idiom.Evaluate(values))
            {
                return false;
            }

            for (int index = 0; index < offsets.Length; index++)
            {
                long value = values[index];
                if (value < int.MinValue || value > int.MaxValue)
                {
                    return false;
                }
            }

            for (int index = 0; index < offsets.Length; index++)
            {
                cells[p + offsets[index]] = (int)values[index];
            }

            return true;
        }

        internal sealed class Match
        {
            internal Match(Idiom idiom, int[] offsets)
            {
                Idiom = idiom;
                Offsets = offsets;
            }

            internal Idiom Idiom { get; private set; }

            // Offset of each named cell from the loop's first cell, in the order the
            // names first appear in the pattern.
            internal int[] Offsets { get; private set; }
        }

        // values holds the named cells in pattern order; Evaluate replaces them with
        // the state after the whole loop, or returns false to decline.
        internal delegate bool Evaluator(long[] values);

        internal sealed class Idiom
        {
            private readonly List<Step> steps = new List<Step>();
            private readonly List<char> names = new List<char>();

            internal Idiom(string name, string pattern, Evaluator evaluate)
            {
                Name = name;
                Pattern = pattern;
                Evaluate = evaluate;
                Compile(pattern);
            }

            internal string Name { get; private set; }

            internal string Pattern { get; private set; }

            internal Evaluator Evaluate { get; private set; }

            internal IList<char> Names
            {
                get { return names; }
            }

            private void Compile(string pattern)
            {
                int cell = -1;
                foreach (char c in pattern)
                {
                    if (c >= 'a' && c <= 'z')
                    {
                        if (!names.Contains(c))
                        {
                            names.Add(c);
                        }

                        cell = names.IndexOf(c);
                        continue;
                    }

                    if (char.IsWhiteSpace(c))
                    {
                        continue;
                    }

                    if (cell < 0)
                    {
                        throw new FormatException("An idiom pattern must name a cell first: " + pattern);
                    }

                    byte op;
                    int argument = 0;
                    switch (c)
                    {
                        case '+':
                            op = BfVm.OpAdd;
                            argument = 1;
                            break;
                        case '-':
                            op = BfVm.OpAdd;
                            argument = -1;
                            break;
                        case '[':
                            op = BfVm.OpOpen;
                            break;
                        case ']':
                            op = BfVm.OpClose;
                            break;
                        default:
                            throw new FormatException("Unsupported idiom command '" + c + "' in " + pattern);
                    }

                    // Consecutive + or - on one cell merge like the compiler does.
                    if (op == BfVm.OpAdd && steps.Count > 0 && steps[steps.Count - 1].Op == BfVm.OpAdd && steps[steps.Count - 1].Cell == cell)
                    {
                        steps[steps.Count - 1] = new Step(BfVm.OpAdd, cell, steps[steps.Count - 1].Argument + argument);
                        continue;
                    }

                    steps.Add(new Step(op, cell, argument));
                }

                if (steps.Count < 2 || steps[0].Op != BfVm.OpOpen || steps[steps.Count - 1].Op != BfVm.OpClose || steps[0].Cell != steps[steps.Count - 1].Cell)
                {
                    throw new FormatException("An idiom must be one loop that starts and ends on its first cell: " + pattern);
                }
            }

            // Returns the offset of every named cell, or null when the compiled loop
            // between open and close is not this idiom.
            internal int[] Bind(byte[] operations, int[] arguments, int open, int close)
            {
                var offsets = new int?[names.Count];
                int index = open;
                int position = 0;
                foreach (Step step in steps)
                {
                    if (index > close)
                    {
                        return null;
                    }

                    if (offsets[step.Cell] == null)
                    {
                        if (operations[index] == BfVm.OpMove)
                        {
                            position += arguments[index];
                            index++;
                        }

                        for (int other = 0; other < offsets.Length; other++)
                        {
                            if (offsets[other] == position)
                            {
                                return null;
                            }
                        }

                        offsets[step.Cell] = position;
                    }
                    else if (offsets[step.Cell] != position)
                    {
                        if (operations[index] != BfVm.OpMove || position + arguments[index] != offsets[step.Cell])
                        {
                            return null;
                        }

                        position += arguments[index];
                        index++;
                    }

                    if (index > close)
                    {
                        return null;
                    }

                    byte actual = operations[index];
                    if (step.Op == BfVm.OpAdd)
                    {
                        if (actual != BfVm.OpAdd || arguments[index] != step.Argument)
                        {
                            return null;
                        }
                    }
                    else if (step.Op == BfVm.OpOpen)
                    {
                        // Inner loops may already have been turned into other
                        // superinstructions by an earlier pass; only raw brackets count.
                        if (actual != BfVm.OpOpen && !(index == open))
                        {
                            return null;
                        }
                    }
                    else if (actual != step.Op)
                    {
                        return null;
                    }

                    index++;
                }

                if (index != close + 1)
                {
                    return null;
                }

                var result = new int[offsets.Length];
                for (int cell = 0; cell < offsets.Length; cell++)
                {
                    result[cell] = offsets[cell].Value;
                }

                return result;
            }

            private struct Step
            {
                internal Step(byte op, int cell, int argument)
                {
                    Op = op;
                    Cell = cell;
                    Argument = argument;
                }

                internal byte Op;
                internal int Cell;
                internal int Argument;
            }
        }
    }
}
