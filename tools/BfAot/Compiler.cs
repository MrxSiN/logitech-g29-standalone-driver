using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace G29.BfAot
{
    // A defect in Brainfuck source text, reported before any code is generated.
    public sealed class BfSourceException : Exception
    {
        public BfSourceException(string message)
            : base(message)
        {
        }
    }

    // Flat operation stream of a Brainfuck program. Reduction rules (these define
    // the execution semantics and must match tests/harness/Runtime/BfVm.cs):
    //  - a run of + and - is one Add; a run of < and > is one Move;
    //  - a run that cancels to zero disappears, and the next command merges with
    //    the operation before it;
    //  - [ and ] are matched here, so an unbalanced program never compiles.
    public sealed class FlatProgram
    {
        public const byte Add = 0;
        public const byte Move = 1;
        public const byte Open = 2;
        public const byte Close = 3;
        public const byte In = 4;
        public const byte Out = 5;

        // Upper bound on accepted source text; it bounds build time and memory.
        public const int MaxSourceBytes = 32 * 1024 * 1024;

        private FlatProgram(byte[] ops, int[] args, int commandCount)
        {
            Ops = ops;
            Args = args;
            CommandCount = commandCount;
        }

        public byte[] Ops { get; private set; }

        // Add: delta. Move: delta. Open: index of the matching Close. Close: index of the matching Open.
        public int[] Args { get; private set; }

        // Number of Brainfuck command characters in the source.
        public int CommandCount { get; private set; }

        // Accepts only the eight commands and CR/LF line endings.
        public static FlatProgram Parse(byte[] source)
        {
            if (source.Length > MaxSourceBytes)
            {
                throw new BfSourceException(string.Format(CultureInfo.InvariantCulture, "The program is {0} bytes; the limit is {1}.", source.Length, MaxSourceBytes));
            }

            var ops = new List<byte>();
            var args = new List<int>();
            var open = new Stack<int>();
            var openLines = new Stack<long>();
            int line = 1, column = 0, commands = 0;
            foreach (byte value in source)
            {
                column++;
                int last = ops.Count - 1;
                switch ((char)value)
                {
                    case '\n':
                        line++;
                        column = 0;
                        break;
                    case '\r':
                        break;
                    case '+':
                    case '-':
                    case '>':
                    case '<':
                    {
                        byte kind = value == '+' || value == '-' ? Add : Move;
                        int delta = value == '+' || value == '>' ? 1 : -1;
                        if (last >= 0 && ops[last] == kind)
                        {
                            args[last] += delta;
                            if (args[last] == 0)
                            {
                                ops.RemoveAt(last);
                                args.RemoveAt(last);
                            }
                        }
                        else
                        {
                            ops.Add(kind);
                            args.Add(delta);
                        }

                        break;
                    }

                    case '[':
                        open.Push(ops.Count);
                        openLines.Push(((long)line << 32) | (uint)column);
                        ops.Add(Open);
                        args.Add(0);
                        break;
                    case ']':
                    {
                        if (open.Count == 0)
                        {
                            throw new BfSourceException(string.Format(CultureInfo.InvariantCulture, "Line {0}, column {1}: unmatched ']'.", line, column));
                        }

                        int start = open.Pop();
                        openLines.Pop();
                        args[start] = ops.Count;
                        ops.Add(Close);
                        args.Add(start);
                        break;
                    }

                    case ',':
                        ops.Add(In);
                        args.Add(0);
                        break;
                    case '.':
                        ops.Add(Out);
                        args.Add(0);
                        break;
                    default:
                        throw new BfSourceException(string.Format(CultureInfo.InvariantCulture, "Line {0}, column {1}: byte 0x{2:X2} is not a Brainfuck command. Only ><+-.,[] and line endings are allowed.", line, column, value));
                }

                if (value != '\n' && value != '\r')
                {
                    commands++;
                }
            }

            if (open.Count != 0)
            {
                long position = openLines.Pop();
                throw new BfSourceException(string.Format(CultureInfo.InvariantCulture, "Line {0}, column {1}: unmatched '['.", position >> 32, position & 0xFFFFFFFF));
            }

            return new FlatProgram(ops.ToArray(), args.ToArray(), commands);
        }
    }

    public enum NodeKind
    {
        Add,
        Move,
        In,
        Out,
        Loop,
        Clear,
        MulAdd,
        Idiom
    }

    // One operation of the loop tree built from a FlatProgram.
    public sealed class Node
    {
        public NodeKind Kind;

        // Add, Move: delta. Clear: the counter step (+1 or -1).
        public int Value;

        // Loop and Idiom (the plain fallback of an idiom).
        public List<Node> Body;

        // MulAdd: [0] is the counter (offset 0, factor +1 or -1), then the
        // targets in first-touch order. Idiom: the offset of each idiom cell.
        public int[] Offsets;
        public int[] Factors;
        public LoopIdiom Idiom;

        // Flat operations covered by this node (the size measure for splitting).
        public int Size;

        // Loop kinds: the body returns the pointer to where it started and every
        // nested loop does too, so the pointer is the same at every iteration.
        public bool Balanced;

        // Balanced loop kinds: every cell offset (relative to the loop cell) the
        // loop may write. Null when unknown.
        public HashSet<int> Writes;
    }

    // A loop shape with a closed form. Each one is exactly the plain loop when its
    // preconditions hold; otherwise the generated code runs the loop plainly. The
    // closed forms are in src/bridge/common/bfrt.h (bf_idiom_*).
    public sealed class LoopIdiom
    {
        internal static readonly LoopIdiom[] Known =
        {
            // race: m += min(a, b); a and b drop by the minimum, a's excess moves to s.
            new LoopIdiom("race", "a[b[-f+g+b]g[-b+g]g+f[a-b-m+g-f[-]]g[a[-s+a]g-]a]"),
            // divmod: q += (r + n) / (r + c); r = (r + n) % (r + c); c = divisor - r.
            new LoopIdiom("divmod", "n[n-r+c-c[-t+e+c]e[-c+e]e+t[e-t[-]]e[q+r[-c+r]e-]n]"),
            // multiply: p += a * b.
            new LoopIdiom("multiply", "a[-b[-p+t+b]t[-b+t]a]")
        };

        private readonly List<byte> stepOps = new List<byte>();
        private readonly List<int> stepCells = new List<int>();
        private readonly List<int> stepArgs = new List<int>();

        private LoopIdiom(string name, string pattern)
        {
            Name = name;
            var names = new List<char>();
            int cell = -1;
            foreach (char c in pattern)
            {
                if (c >= 'a' && c <= 'z')
                {
                    cell = names.IndexOf(c);
                    if (cell < 0)
                    {
                        names.Add(c);
                        cell = names.Count - 1;
                    }

                    continue;
                }

                byte op = c == '+' || c == '-' ? FlatProgram.Add : c == '[' ? FlatProgram.Open : FlatProgram.Close;
                int argument = c == '+' ? 1 : c == '-' ? -1 : 0;
                int last = stepOps.Count - 1;
                if (op == FlatProgram.Add && last >= 0 && stepOps[last] == FlatProgram.Add && stepCells[last] == cell)
                {
                    stepArgs[last] += argument;
                    continue;
                }

                stepOps.Add(op);
                stepCells.Add(cell);
                stepArgs.Add(argument);
            }

            CellCount = names.Count;
        }

        public string Name { get; private set; }

        public int CellCount { get; private set; }

        // The cell offsets when ops[open..close] is this idiom, otherwise null.
        internal int[] Bind(byte[] ops, int[] args, int open, int close)
        {
            var offsets = new int[CellCount];
            var known = new bool[CellCount];
            int index = open, position = 0;
            for (int s = 0; s < stepOps.Count; s++)
            {
                int cell = stepCells[s];
                if (index > close)
                {
                    return null;
                }

                if (!known[cell])
                {
                    if (ops[index] == FlatProgram.Move)
                    {
                        position += args[index];
                        index++;
                    }

                    for (int other = 0; other < CellCount; other++)
                    {
                        if (known[other] && offsets[other] == position)
                        {
                            return null;
                        }
                    }

                    offsets[cell] = position;
                    known[cell] = true;
                }
                else if (offsets[cell] != position)
                {
                    if (ops[index] != FlatProgram.Move || position + args[index] != offsets[cell])
                    {
                        return null;
                    }

                    position += args[index];
                    index++;
                }

                if (index > close)
                {
                    return null;
                }

                byte actual = ops[index];
                if (stepOps[s] == FlatProgram.Add)
                {
                    if (actual != FlatProgram.Add || args[index] != stepArgs[s])
                    {
                        return null;
                    }
                }
                else if (stepOps[s] == FlatProgram.Open)
                {
                    if (actual != FlatProgram.Open && index != open)
                    {
                        return null;
                    }
                }
                else if (actual != stepOps[s])
                {
                    return null;
                }

                index++;
            }

            return index == close + 1 ? offsets : null;
        }
    }

    // Builds the loop tree and classifies every loop, outermost first, the same
    // way as the reference interpreter: idiom, then clear ([-] or [+]), then
    // multiply-add (only adds and moves, pointer back at the start, counter
    // step +1 or -1).
    public static class TreeBuilder
    {
        public static List<Node> Build(FlatProgram program)
        {
            byte[] ops = program.Ops;
            int[] args = program.Args;
            var kinds = new NodeKind[ops.Length];
            var idioms = new Dictionary<int, KeyValuePair<LoopIdiom, int[]>>();
            var mulAdds = new Dictionary<int, KeyValuePair<int[], int[]>>();
            for (int open = 0; open < ops.Length; open++)
            {
                if (ops[open] != FlatProgram.Open)
                {
                    continue;
                }

                int close = args[open];
                kinds[open] = NodeKind.Loop;
                bool matched = false;
                foreach (LoopIdiom idiom in LoopIdiom.Known)
                {
                    int[] offsets = idiom.Bind(ops, args, open, close);
                    if (offsets != null)
                    {
                        kinds[open] = NodeKind.Idiom;
                        idioms[open] = new KeyValuePair<LoopIdiom, int[]>(idiom, offsets);
                        matched = true;
                        break;
                    }
                }

                if (matched)
                {
                    continue;
                }

                if (close == open + 2 && ops[open + 1] == FlatProgram.Add && (args[open + 1] == 1 || args[open + 1] == -1))
                {
                    kinds[open] = NodeKind.Clear;
                    continue;
                }

                KeyValuePair<int[], int[]> mulAdd;
                if (TryMulAdd(ops, args, open, close, out mulAdd))
                {
                    kinds[open] = NodeKind.MulAdd;
                    mulAdds[open] = mulAdd;
                }
            }

            int position = 0;
            List<Node> root = BuildSequence(ops, args, kinds, idioms, mulAdds, ref position, ops.Length);
            int net;
            bool balanced;
            Analyze(root, out net, out balanced, new HashSet<int>());
            return root;
        }

        private static bool TryMulAdd(byte[] ops, int[] args, int open, int close, out KeyValuePair<int[], int[]> result)
        {
            result = default(KeyValuePair<int[], int[]>);
            var order = new List<int>();
            var factors = new Dictionary<int, long>();
            int offset = 0;
            for (int index = open + 1; index < close; index++)
            {
                if (ops[index] == FlatProgram.Add)
                {
                    long current;
                    if (!factors.TryGetValue(offset, out current))
                    {
                        order.Add(offset);
                    }

                    factors[offset] = current + args[index];
                }
                else if (ops[index] == FlatProgram.Move)
                {
                    offset += args[index];
                }
                else
                {
                    return false;
                }
            }

            long step;
            if (offset != 0 || !factors.TryGetValue(0, out step) || (step != 1 && step != -1))
            {
                return false;
            }

            var offsets = new List<int> { 0 };
            var multipliers = new List<int> { (int)step };
            foreach (int key in order)
            {
                long factor = factors[key];
                if (key == 0 || factor == 0)
                {
                    continue;
                }

                if (factor < int.MinValue || factor > int.MaxValue)
                {
                    return false;
                }

                offsets.Add(key);
                multipliers.Add((int)factor);
            }

            result = new KeyValuePair<int[], int[]>(offsets.ToArray(), multipliers.ToArray());
            return true;
        }

        private static List<Node> BuildSequence(byte[] ops, int[] args, NodeKind[] kinds, Dictionary<int, KeyValuePair<LoopIdiom, int[]>> idioms, Dictionary<int, KeyValuePair<int[], int[]>> mulAdds, ref int index, int end)
        {
            var nodes = new List<Node>();
            while (index < end)
            {
                byte op = ops[index];
                switch (op)
                {
                    case FlatProgram.Add:
                        nodes.Add(new Node { Kind = NodeKind.Add, Value = args[index], Size = 1 });
                        index++;
                        break;
                    case FlatProgram.Move:
                        nodes.Add(new Node { Kind = NodeKind.Move, Value = args[index], Size = 1 });
                        index++;
                        break;
                    case FlatProgram.In:
                        nodes.Add(new Node { Kind = NodeKind.In, Size = 1 });
                        index++;
                        break;
                    case FlatProgram.Out:
                        nodes.Add(new Node { Kind = NodeKind.Out, Size = 1 });
                        index++;
                        break;
                    case FlatProgram.Open:
                    {
                        int open = index, close = args[index];
                        var node = new Node { Kind = kinds[open] };
                        switch (node.Kind)
                        {
                            case NodeKind.Clear:
                                node.Value = args[open + 1];
                                node.Size = 1;
                                index = close + 1;
                                break;
                            case NodeKind.MulAdd:
                                node.Offsets = mulAdds[open].Key;
                                node.Factors = mulAdds[open].Value;
                                node.Size = node.Offsets.Length;
                                index = close + 1;
                                break;
                            default:
                                if (node.Kind == NodeKind.Idiom)
                                {
                                    node.Idiom = idioms[open].Key;
                                    node.Offsets = idioms[open].Value;
                                }

                                index = open + 1;
                                node.Body = BuildSequence(ops, args, kinds, idioms, mulAdds, ref index, close);
                                index = close + 1;
                                break;
                        }

                        nodes.Add(node);
                        break;
                    }

                    default:
                        throw new InvalidOperationException("Unexpected ']' in the flat program.");
                }
            }

            return nodes;
        }

        // Fills Size, Balanced and Writes of every loop. net: pointer displacement
        // of the sequence; balanced: false when a nested loop moves the pointer
        // by an unknown amount; writes: offsets written (null once unknown).
        private static int Analyze(List<Node> sequence, out int net, out bool balanced, HashSet<int> writes)
        {
            int position = 0, size = 0;
            balanced = true;
            foreach (Node node in sequence)
            {
                switch (node.Kind)
                {
                    case NodeKind.Move:
                        position += node.Value;
                        break;
                    case NodeKind.Add:
                    case NodeKind.In:
                        Write(writes, balanced, position);
                        break;
                    case NodeKind.Clear:
                        node.Balanced = true;
                        node.Writes = new HashSet<int> { 0 };
                        Write(writes, balanced, position);
                        break;
                    case NodeKind.MulAdd:
                        node.Balanced = true;
                        node.Writes = new HashSet<int>(node.Offsets);
                        foreach (int offset in node.Offsets)
                        {
                            Write(writes, balanced, position + offset);
                        }

                        break;
                    case NodeKind.Loop:
                    case NodeKind.Idiom:
                    {
                        int bodyNet;
                        bool bodyBalanced;
                        var bodyWrites = new HashSet<int>();
                        node.Size = 1 + Analyze(node.Body, out bodyNet, out bodyBalanced, bodyWrites);
                        node.Balanced = bodyBalanced && bodyNet == 0;
                        if (node.Balanced)
                        {
                            bodyWrites.Add(0);
                            if (node.Kind == NodeKind.Idiom)
                            {
                                bodyWrites.UnionWith(node.Offsets);
                            }

                            node.Writes = bodyWrites;
                            foreach (int offset in bodyWrites)
                            {
                                Write(writes, balanced, position + offset);
                            }
                        }
                        else
                        {
                            balanced = false;
                        }

                        break;
                    }
                }

                size += node.Size;
            }

            net = position;
            return size;
        }

        private static void Write(HashSet<int> writes, bool balanced, int offset)
        {
            if (balanced)
            {
                writes.Add(offset);
            }
        }
    }

    public sealed class CompileStatistics
    {
        public int SourceBytes;
        public int Commands;
        public int FlatOperations;
        public int Loops;
        public int Clears;
        public int MulAdds;
        public Dictionary<string, int> Idioms = new Dictionary<string, int>();
        public int DeadLoops;
        public int Functions;
        public int GeneratedStatements;
        public int KnownPositionOperations;
        public int RelativeOperations;
        public int TapeChecks;
        public int OverflowChecks;
        public int ConstantStores;
        public int StaticFaults;
        public long GeneratedBytes;

        public string Format()
        {
            var text = new StringBuilder();
            Line(text, "source_bytes", SourceBytes);
            Line(text, "commands", Commands);
            Line(text, "flat_operations", FlatOperations);
            Line(text, "loops_plain", Loops);
            Line(text, "loops_clear", Clears);
            Line(text, "loops_muladd", MulAdds);
            foreach (KeyValuePair<string, int> idiom in Idioms)
            {
                Line(text, "loops_idiom_" + idiom.Key, idiom.Value);
            }

            Line(text, "loops_dead_removed", DeadLoops);
            Line(text, "functions", Functions);
            Line(text, "generated_statements", GeneratedStatements);
            Line(text, "operations_static_pointer", KnownPositionOperations);
            Line(text, "operations_relative_pointer", RelativeOperations);
            Line(text, "runtime_tape_checks", TapeChecks);
            Line(text, "runtime_overflow_checks", OverflowChecks);
            Line(text, "constant_stores", ConstantStores);
            Line(text, "static_faults", StaticFaults);
            text.Append("generated_c_bytes=").Append(GeneratedBytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return text.ToString();
        }

        private static void Line(StringBuilder text, string name, int value)
        {
            text.Append(name).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    // Translates the loop tree into C for src/bridge/common/bfrt.h.
    //
    // Pointer model: while the pointer's absolute position is known at compile
    // time (from the start of the program and through balanced loops), cells are
    // addressed as constants and no tape check is emitted, because the position
    // is proven in range. After a loop that moves the pointer by an unknown
    // amount, cells are addressed relative to the runtime pointer p; a check is
    // emitted only where the pointer reaches a new extreme within the current
    // stretch, which is exactly where the reference interpreter could fault.
    //
    // Value model: within straight-line code the compiler tracks cells whose
    // value is known (after a clear, a constant store, or at a loop exit) and
    // folds arithmetic on them into constant stores without overflow checks.
    // A loop whose cell is known to be zero is removed.
    //
    // Large loop bodies and long sequences go into separate functions so the C
    // compiler never sees one enormous function.
    public sealed class CEmitter
    {
        public const int TapeLength = 131072;

        private readonly int threshold;
        private readonly string prefix;
        private readonly List<string> prototypes;
        private readonly List<string> definitions;
        private readonly CompileStatistics stats;
        private int nextFunction;

        private CEmitter(string prefix, int threshold, CompileStatistics stats, List<string> prototypes, List<string> definitions)
        {
            this.prefix = prefix;
            this.threshold = threshold;
            this.stats = stats;
            this.prototypes = prototypes;
            this.definitions = definitions;
        }

        // Appends the prototypes and definitions of one compiled program: its
        // functions (each defined before its first caller) and then the entry
        // point `int entry(bf_context *c)`.
        public static void Emit(List<Node> program, string entry, int threshold, CompileStatistics stats, List<string> prototypes, List<string> definitions)
        {
            var emitter = new CEmitter(entry, threshold, stats, prototypes, definitions);
            var state = new State { Known = true, Values = new Dictionary<int, int>() };
            string top = emitter.EmitFunction(delegate(Function function) { emitter.EmitSequence(function, program, state, true); }, state);
            prototypes.Add("int " + entry + "(bf_context *c);");
            definitions.Add("int " + entry + "(bf_context *c)\n{\n int p = 0;\n int r = " + top + "(c, c->tape, &p);\n" +
                " return r == 0 ? BF_FINISHED : r == BFX_STOP ? BF_STOPPED : BF_FAULT;\n}\n");
            CountLoops(program, stats);
        }

        private static void CountLoops(List<Node> sequence, CompileStatistics stats)
        {
            foreach (Node node in sequence)
            {
                switch (node.Kind)
                {
                    case NodeKind.Loop:
                        stats.Loops++;
                        break;
                    case NodeKind.Clear:
                        stats.Clears++;
                        break;
                    case NodeKind.MulAdd:
                        stats.MulAdds++;
                        break;
                    case NodeKind.Idiom:
                        int count;
                        stats.Idioms.TryGetValue(node.Idiom.Name, out count);
                        stats.Idioms[node.Idiom.Name] = count + 1;
                        break;
                }

                if (node.Body != null)
                {
                    CountLoops(node.Body, stats);
                }
            }
        }

        private sealed class Function
        {
            public readonly StringBuilder Text = new StringBuilder();
            public int Cost;
            public int Depth = 1;
            public bool UsesResult;
            public bool UsesOverflow;
            public bool UsesTape;
        }

        private sealed class State
        {
            // Known: the pointer is Pos (absolute). Otherwise it is p + D at run
            // time, and p + Lo .. p + Hi are proven inside the tape.
            public bool Known;
            public int Pos;
            public int D;
            public int Lo;
            public int Hi;

            // Cells with a compile-time value, keyed by absolute index (Known) or
            // by offset from p.
            public Dictionary<int, int> Values;

            public State Clone()
            {
                return new State { Known = Known, Pos = Pos, D = D, Lo = Lo, Hi = Hi, Values = new Dictionary<int, int>(Values) };
            }

            public void CopyFrom(State other)
            {
                Known = other.Known;
                Pos = other.Pos;
                D = other.D;
                Lo = other.Lo;
                Hi = other.Hi;
                Values = other.Values;
            }

            public int Key
            {
                get { return Known ? Pos : D; }
            }
        }

        private static string N(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Index(State s, int offset)
        {
            if (s.Known)
            {
                return N(s.Pos + offset);
            }

            int relative = s.D + offset;
            return relative == 0 ? "p" : relative > 0 ? "p + " + N(relative) : "p - " + N(-relative);
        }

        private static string Cell(State s, int offset)
        {
            return "t[" + Index(s, offset) + "]";
        }

        private void Line(Function f, string text)
        {
            f.Text.Append(' ', f.Depth).Append(text).Append('\n');
            stats.GeneratedStatements++;
        }

        // Emits a function whose body the callback writes; returns its name. The
        // function receives p through pp and stores it back on the way out.
        private string EmitFunction(Action<Function> body, State state)
        {
            string name = prefix + "_" + N(nextFunction++);
            var f = new Function();
            body(f);
            Materialize(f, state);
            stats.Functions++;
            var output = new StringBuilder();
            prototypes.Add("int " + name + "(bf_context *c, int32_t *t, int *pp);");
            output.Append("int ").Append(name).Append("(bf_context *c, int32_t *t, int *pp)\n{\n int p = *pp;\n");
            if (f.UsesResult)
            {
                output.Append(" int r;\n");
            }

            output.Append(" (void)c;\n (void)t;\n").Append(f.Text).Append(" *pp = p;\n return 0;\n");
            if (f.UsesOverflow)
            {
                output.Append("ovf:\n return bf_fault_overflow(c);\n");
            }

            if (f.UsesTape)
            {
                output.Append("tape:\n return bf_fault_tape(c);\n");
            }

            output.Append("}\n");
            definitions.Add(output.ToString());
            return name;
        }

        private void Call(Function f, string name)
        {
            f.UsesResult = true;
            Line(f, "if ((r = " + name + "(c, t, &p)) != 0) return r;");
        }

        private int Cost(Node node)
        {
            return node.Size > threshold ? 4 : Math.Max(1, node.Size);
        }

        // spill: the sequence may be split into chunk functions. Inline loop
        // bodies never spill, so the pointer stays where the loop test expects it.
        private void EmitSequence(Function f, List<Node> nodes, State s, bool spill)
        {
            int index = 0;
            while (index < nodes.Count)
            {
                int cost = Cost(nodes[index]);
                if (spill && f.Cost > 0 && f.Cost + cost > threshold)
                {
                    int end = index, total = 0;
                    while (end < nodes.Count && (end == index || total + Cost(nodes[end]) <= threshold))
                    {
                        total += Cost(nodes[end]);
                        end++;
                    }

                    List<Node> chunk = nodes.GetRange(index, end - index);
                    Materialize(f, s);
                    State inner = s.Clone();
                    string name = EmitFunction(delegate(Function g) { EmitSequence(g, chunk, inner, true); }, inner);
                    Call(f, name);
                    f.Cost += 1;
                    s.CopyFrom(inner);
                    index = end;
                    continue;
                }

                EmitNode(f, nodes[index], s);
                if (spill)
                {
                    f.Cost += cost;
                }

                index++;
            }
        }

        private void EmitNode(Function f, Node node, State s)
        {
            if (s.Known)
            {
                stats.KnownPositionOperations++;
            }
            else
            {
                stats.RelativeOperations++;
            }

            switch (node.Kind)
            {
                case NodeKind.Move:
                    EmitMove(f, s, node.Value);
                    break;
                case NodeKind.Add:
                    EmitAdd(f, s, 0, node.Value);
                    break;
                case NodeKind.In:
                    f.UsesResult = true;
                    Line(f, "if ((r = bf_in(c, &" + Cell(s, 0) + ")) != 0) return r;");
                    s.Values.Remove(s.Key);
                    break;
                case NodeKind.Out:
                {
                    int value;
                    f.UsesResult = true;
                    Line(f, "if ((r = bf_out(c, " + (s.Values.TryGetValue(s.Key, out value) ? N(value) : Cell(s, 0)) + ")) != 0) return r;");
                    break;
                }

                case NodeKind.Clear:
                    EmitClear(f, s, node.Value);
                    break;
                case NodeKind.MulAdd:
                    EmitMulAdd(f, s, node);
                    break;
                case NodeKind.Loop:
                case NodeKind.Idiom:
                    EmitLoop(f, s, node);
                    break;
            }
        }

        private void EmitMove(Function f, State s, int delta)
        {
            if (s.Known)
            {
                s.Pos += delta;
                if (s.Pos < 0 || s.Pos >= TapeLength)
                {
                    StaticFault(f, "bf_fault_tape");
                }

                return;
            }

            s.D += delta;
            if (s.D < s.Lo || s.D > s.Hi)
            {
                TapeCheck(f, s, 0);
                s.Lo = Math.Min(s.Lo, s.D);
                s.Hi = Math.Max(s.Hi, s.D);
            }
        }

        private void TapeCheck(Function f, State s, int offset)
        {
            f.UsesTape = true;
            stats.TapeChecks++;
            Line(f, "if ((unsigned)(" + Index(s, offset) + ") >= BF_TAPE_LENGTH) goto tape;");
        }

        // A fault the compiler proved: kept as a runtime call so the generated
        // code stays well-formed; the production program must have none.
        private void StaticFault(Function f, string handler)
        {
            stats.StaticFaults++;
            Line(f, "if (" + handler + "(c) != 0) return BFX_FAULT;");
        }

        private void EmitAdd(Function f, State s, int offset, long delta)
        {
            int value;
            int key = s.Key + offset;
            string cell = Cell(s, offset);
            if (s.Values.TryGetValue(key, out value))
            {
                long result = value + delta;
                if (result < int.MinValue || result > int.MaxValue)
                {
                    StaticFault(f, "bf_fault_overflow");
                    s.Values.Remove(key);
                    return;
                }

                stats.ConstantStores++;
                Line(f, cell + " = " + ConstantLiteral((int)result) + ";");
                s.Values[key] = (int)result;
                return;
            }

            f.UsesOverflow = true;
            stats.OverflowChecks++;
            if (delta >= 1 && delta <= int.MaxValue)
            {
                Line(f, "BF_INC(" + cell + ", " + N(delta) + ");");
            }
            else if (delta <= -1 && delta >= -(long)int.MaxValue)
            {
                Line(f, "BF_DEC(" + cell + ", " + N(-delta) + ");");
            }
            else
            {
                // |delta| >= 2^31 (only from a constant multiply-add)
                Line(f, "{ int64_t v = (int64_t)" + cell + " + (" + N(delta) + "LL); if (v < INT32_MIN || v > INT32_MAX) goto ovf; " + cell + " = (int32_t)v; }");
            }
        }
        // INT32_MIN cannot be written as a plain C literal.
        private static string ConstantLiteral(int value)
        {
            return value == int.MinValue ? "INT32_MIN" : N(value);
        }

        private void EmitClear(Function f, State s, int step)
        {
            int value;
            if (s.Values.TryGetValue(s.Key, out value))
            {
                if (value == 0)
                {
                    stats.DeadLoops++;
                    return;
                }

                if ((step < 0) != (value > 0))
                {
                    StaticFault(f, "bf_fault_overflow");
                }
            }
            else
            {
                f.UsesOverflow = true;
                stats.OverflowChecks++;
                Line(f, "if (" + Cell(s, 0) + (step < 0 ? " < 0" : " > 0") + ") goto ovf;");
            }

            stats.ConstantStores++;
            Line(f, Cell(s, 0) + " = 0;");
            s.Values[s.Key] = 0;
        }

        private void EmitMulAdd(Function f, State s, Node node)
        {
            int value;
            bool constant = s.Values.TryGetValue(s.Key, out value);
            if (constant && value == 0)
            {
                stats.DeadLoops++;
                return;
            }

            if (constant)
            {
                EmitConstantMulAdd(f, s, node, value);
                return;
            }

            f.UsesOverflow = true;
            stats.OverflowChecks++;
            Line(f, "if (" + Cell(s, 0) + " != 0) {");
            f.Depth++;
            Line(f, "int64_t n = " + Cell(s, 0) + ", v;");
            // Counting away from zero would overflow before the loop ends.
            Line(f, node.Factors[0] < 0 ? "if (n < 0) goto ovf;" : "if (n > 0) goto ovf; n = -n;");
            for (int item = 1; item < node.Offsets.Length; item++)
            {
                int offset = node.Offsets[item];
                if (s.Known)
                {
                    int target = s.Pos + offset;
                    if (target < 0 || target >= TapeLength)
                    {
                        StaticFault(f, "bf_fault_tape");
                    }
                }
                else if (s.D + offset < s.Lo || s.D + offset > s.Hi)
                {
                    // conditional, so it does not widen the proven range
                    TapeCheck(f, s, offset);
                }

                stats.OverflowChecks++;
                Line(f, "BF_MULADD(" + Cell(s, offset) + ", " + N(node.Factors[item]) + ");");
                s.Values.Remove(s.Key + offset);
            }

            Line(f, Cell(s, 0) + " = 0;");
            f.Depth--;
            Line(f, "}");
            s.Values[s.Key] = 0;
        }

        // The counter is a compile-time constant, so the loop runs |value| times:
        // each target gets one constant addition.
        private void EmitConstantMulAdd(Function f, State s, Node node, int value)
        {
            if ((node.Factors[0] < 0) != (value > 0))
            {
                StaticFault(f, "bf_fault_overflow");
                s.Values.Remove(s.Key);
                return;
            }

            long count = Math.Abs((long)value);
            for (int item = 1; item < node.Offsets.Length; item++)
            {
                int offset = node.Offsets[item];
                if (s.Known)
                {
                    int target = s.Pos + offset;
                    if (target < 0 || target >= TapeLength)
                    {
                        StaticFault(f, "bf_fault_tape");
                    }
                }
                else if (s.D + offset < s.Lo || s.D + offset > s.Hi)
                {
                    TapeCheck(f, s, offset);
                    s.Lo = Math.Min(s.Lo, s.D + offset);
                    s.Hi = Math.Max(s.Hi, s.D + offset);
                }

                EmitAdd(f, s, offset, count * node.Factors[item]);
            }

            stats.ConstantStores++;
            Line(f, Cell(s, 0) + " = 0;");
            s.Values[s.Key] = 0;
        }
        private void EmitLoop(Function f, State s, Node node)
        {
            int value;
            if (s.Values.TryGetValue(s.Key, out value) && value == 0)
            {
                // Every loop kind is skipped when its cell is zero (an idiom's
                // preconditions all require a positive first cell).
                stats.DeadLoops++;
                return;
            }

            bool separate = node.Size > threshold;
            if (!node.Balanced)
            {
                ToRelative(f, s);
                Materialize(f, s);
            }
            else if (separate)
            {
                Materialize(f, s);
            }

            if (node.Kind == NodeKind.Idiom)
            {
                var arguments = new StringBuilder();
                foreach (int offset in node.Offsets)
                {
                    arguments.Append(", ").Append(Index(s, offset));
                }

                Line(f, "if (!bf_idiom_" + node.Idiom.Name + "(t" + arguments + ")) {");
                f.Depth++;
            }

            Line(f, "while (" + Cell(s, 0) + ") {");
            f.Depth++;
            Line(f, "BF_ITERATION();");
            State body = s.Clone();
            body.Values = new Dictionary<int, int>();
            if (!node.Balanced)
            {
                body.Lo = 0;
                body.Hi = 0;
            }

            if (separate)
            {
                string name = EmitFunction(delegate(Function g) { EmitSequence(g, node.Body, body, true); }, body);
                Call(f, name);
            }
            else
            {
                EmitSequence(f, node.Body, body, false);
                if (!node.Balanced)
                {
                    Materialize(f, body);
                }
            }

            f.Depth--;
            Line(f, "}");
            if (node.Kind == NodeKind.Idiom)
            {
                f.Depth--;
                Line(f, "}");
            }

            if (node.Balanced)
            {
                foreach (int offset in node.Writes)
                {
                    s.Values.Remove(s.Key + offset);
                }
            }
            else
            {
                s.Lo = 0;
                s.Hi = 0;
                s.Values.Clear();
            }

            s.Values[s.Key] = 0;
        }

        // Leaves the compile-time-known pointer model: p gets the position, and
        // the whole tape is proven valid relative to it.
        private void ToRelative(Function f, State s)
        {
            if (!s.Known)
            {
                return;
            }

            Line(f, "p = " + N(s.Pos) + ";");
            var values = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> pair in s.Values)
            {
                values[pair.Key - s.Pos] = pair.Value;
            }

            s.Values = values;
            s.Lo = -s.Pos;
            s.Hi = TapeLength - 1 - s.Pos;
            s.D = 0;
            s.Known = false;
        }

        // Applies the pending offset to p.
        private void Materialize(Function f, State s)
        {
            if (s.Known || s.D == 0)
            {
                return;
            }

            Line(f, s.D > 0 ? "p += " + N(s.D) + ";" : "p -= " + N(-(long)s.D) + ";");
            var values = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> pair in s.Values)
            {
                values[pair.Key - s.D] = pair.Value;
            }

            s.Values = values;
            s.Lo -= s.D;
            s.Hi -= s.D;
            s.D = 0;
        }
    }

    // Validation and compilation of one or more programs into one C file.
    public static class AotCompiler
    {
        // Flat operations per generated function. Smaller functions compile much
        // faster with /O2 (the optimizer is superlinear in function size).
        public const int DefaultThreshold = 200;

        public static List<Node> Load(byte[] source, CompileStatistics stats)
        {
            FlatProgram flat = FlatProgram.Parse(source);
            stats.SourceBytes = source.Length;
            stats.Commands = flat.CommandCount;
            stats.FlatOperations = flat.Ops.Length;
            return TreeBuilder.Build(flat);
        }

        public static string Banner(string description)
        {
            return "/* Generated by tools/BfAot from " + description + ". Do not edit: the build regenerates it. */\n";
        }
    }
}
