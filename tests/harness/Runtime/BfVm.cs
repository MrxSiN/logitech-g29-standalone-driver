using System;
using System.Collections.Generic;

namespace G29.Bridge.Runtime
{
    // BF32-G29: classic Brainfuck (only + - < > [ ] , . are commands) with checked,
    // non-wrapping signed 32-bit cells, a bounded 131072-cell tape, byte input and
    // byte output. The program runs once and persists: ',' blocks for the next input
    // byte, so tape state survives between host events.
    //
    // Liveness: the step budget applies between two input reads, not to the whole
    // lifetime. A program that computes too long without asking for input fails.
    //
    // Speed: loops that match proven idioms (clear loops, multiply-add loops and the
    // canonical library loops in BfIdioms) run as single superinstructions. Each
    // one produces exactly the tape state plain execution would, and falls back to
    // plain execution whenever its preconditions do not hold.
    public sealed class BfVm : IBfMachine
    {
        public const int TapeLength = 131072;
        public const long DefaultStepBudget = 50000000;

        internal const byte OpAdd = 0;
        internal const byte OpMove = 1;
        internal const byte OpOpen = 2;
        internal const byte OpClose = 3;
        internal const byte OpRead = 4;
        internal const byte OpWrite = 5;
        internal const byte OpClear = 6;
        internal const byte OpMulAdd = 7;
        internal const byte OpIdiom = 8;

        private readonly BfProgram program;
        private readonly int[] tape = new int[TapeLength];
        private readonly Func<int> read;
        private readonly Action<byte> write;
        private long stepBudget;
        private int pointer;
        private int counter;
        private long totalSteps;
        private long stepsSinceRead;
        private long superinstructions;

        // read returns the next input byte (0..255), blocking as needed, or -1 when
        // the host is shutting the program down.
        public BfVm(BfProgram program, Func<int> read, Action<byte> write, long stepBudget)
        {
            if (program == null)
            {
                throw new ArgumentNullException("program");
            }

            if (read == null)
            {
                throw new ArgumentNullException("read");
            }

            if (write == null)
            {
                throw new ArgumentNullException("write");
            }

            if (stepBudget <= 0)
            {
                throw new ArgumentOutOfRangeException("stepBudget", stepBudget, "Step budget must be positive.");
            }

            this.program = program;
            this.read = read;
            this.write = write;
            this.stepBudget = stepBudget;
        }

        public long TotalSteps
        {
            get { return totalSteps; }
        }

        public long Superinstructions
        {
            get { return superinstructions; }
        }

        public int Pointer
        {
            get { return pointer; }
        }

        public bool Finished
        {
            get { return counter >= program.Length; }
        }

        public int Cell(int index)
        {
            return tape[index];
        }

        // Test support: preloads a cell before the program runs.
        internal void SetCell(int index, int value)
        {
            tape[index] = value;
        }

        public long StepBudget
        {
            get { return stepBudget; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException("value");
                }

                stepBudget = value;
            }
        }

        // Runs until the program ends (returns true) or the host stops it through
        // read returning -1 (returns false). Program faults throw BfFault.
        public bool Run()
        {
            byte[] operations = program.Operations;
            int[] arguments = program.Arguments;
            int length = operations.Length;
            int[] cells = tape;
            int p = pointer;
            int pc = counter;
            long steps = stepsSinceRead;
            long budget = stepBudget;
            try
            {
                while (pc < length)
                {
                    if (++steps > budget)
                    {
                        throw new BfFault(string.Format("The Brainfuck program ran more than {0} steps without reading input.", budget), pc);
                    }

                    int argument = arguments[pc];
                    switch (operations[pc])
                    {
                        case OpAdd:
                            cells[p] = checked(cells[p] + argument);
                            break;
                        case OpMove:
                            p += argument;
                            if ((uint)p >= TapeLength)
                            {
                                throw new BfFault("The Brainfuck program moved outside its tape.", pc);
                            }

                            break;
                        case OpOpen:
                            if (cells[p] == 0)
                            {
                                pc = argument;
                            }

                            break;
                        case OpClose:
                            if (cells[p] != 0)
                            {
                                pc = argument;
                            }

                            break;
                        case OpRead:
                        {
                            pointer = p;
                            counter = pc;
                            totalSteps += steps;
                            steps = 0;
                            int value = read();
                            if (value < 0)
                            {
                                stepsSinceRead = 0;
                                return false;
                            }

                            if (value > 255)
                            {
                                throw new BfFault("The host supplied an input value that is not a byte.", pc);
                            }

                            cells[p] = value;
                            break;
                        }

                        case OpWrite:
                        {
                            int value = cells[p];
                            if ((uint)value > 255)
                            {
                                throw new BfFault(string.Format("The Brainfuck program tried to output {0}, which is not a byte.", value), pc);
                            }

                            pointer = p;
                            counter = pc;
                            write((byte)value);
                            break;
                        }

                        case OpClear:
                        {
                            int value = cells[p];
                            if (argument < 0 ? value < 0 : value > 0)
                            {
                                // [-] on a negative cell (or [+] on a positive one) would
                                // count past the 32-bit limit.
                                throw new BfFault("A Brainfuck cell overflowed.", pc);
                            }

                            cells[p] = 0;
                            superinstructions++;
                            pc = program.LoopEnd(pc);
                            break;
                        }

                        case OpMulAdd:
                        {
                            int value = cells[p];
                            if (value != 0)
                            {
                                program.MulAdd(argument, cells, p, pc);
                                superinstructions++;
                            }

                            pc = program.LoopEnd(pc);
                            break;
                        }

                        case OpIdiom:
                        {
                            if (BfIdioms.Execute(program.Idiom(argument), cells, p))
                            {
                                superinstructions++;
                                pc = program.LoopEnd(pc);
                            }
                            else if (cells[p] == 0)
                            {
                                pc = program.LoopEnd(pc);
                            }

                            // Otherwise the preconditions failed: fall through into the
                            // loop body and execute it plainly.
                            break;
                        }
                    }

                    pc++;
                }
            }
            catch (OverflowException)
            {
                throw new BfFault("A Brainfuck cell overflowed.", pc);
            }
            finally
            {
                pointer = p;
                counter = pc;
                totalSteps += steps;
                stepsSinceRead = steps;
            }

            return true;
        }
    }

    public sealed class BfFault : Exception
    {
        public BfFault(string message, int operation)
            : base(message)
        {
            Operation = operation;
        }

        public int Operation { get; private set; }
    }

    // A compiled program: merged +/- and </> runs, matched brackets, and loop
    // superinstructions. Shared by every VM that runs the same source.
    public sealed class BfProgram
    {
        private readonly byte[] operations;
        private readonly int[] arguments;
        private readonly int[] loopEnds;
        private readonly List<int[]> mulAddOffsets = new List<int[]>();
        private readonly List<int[]> mulAddFactors = new List<int[]>();
        private readonly List<BfIdioms.Match> idioms = new List<BfIdioms.Match>();

        public BfProgram(string source)
            : this(source, true)
        {
        }

        public BfProgram(string source, bool optimize)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            var ops = new List<byte>();
            var args = new List<int>();
            var open = new Stack<int>();
            foreach (char command in source)
            {
                int last = ops.Count - 1;
                switch (command)
                {
                    case '+':
                    case '-':
                    {
                        int delta = command == '+' ? 1 : -1;
                        if (last >= 0 && ops[last] == BfVm.OpAdd)
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
                            ops.Add(BfVm.OpAdd);
                            args.Add(delta);
                        }

                        break;
                    }

                    case '>':
                    case '<':
                    {
                        int step = command == '>' ? 1 : -1;
                        if (last >= 0 && ops[last] == BfVm.OpMove)
                        {
                            args[last] += step;
                            if (args[last] == 0)
                            {
                                ops.RemoveAt(last);
                                args.RemoveAt(last);
                            }
                        }
                        else
                        {
                            ops.Add(BfVm.OpMove);
                            args.Add(step);
                        }

                        break;
                    }

                    case '[':
                        open.Push(ops.Count);
                        ops.Add(BfVm.OpOpen);
                        args.Add(0);
                        break;
                    case ']':
                    {
                        if (open.Count == 0)
                        {
                            throw new FormatException("The Brainfuck program has an unmatched ']'.");
                        }

                        int start = open.Pop();
                        args[start] = ops.Count;
                        ops.Add(BfVm.OpClose);
                        args.Add(start);
                        break;
                    }

                    case ',':
                        ops.Add(BfVm.OpRead);
                        args.Add(0);
                        break;
                    case '.':
                        ops.Add(BfVm.OpWrite);
                        args.Add(0);
                        break;
                }
            }

            if (open.Count != 0)
            {
                throw new FormatException("The Brainfuck program has an unmatched '['.");
            }

            operations = ops.ToArray();
            arguments = args.ToArray();
            loopEnds = new int[operations.Length];
            for (int index = 0; index < operations.Length; index++)
            {
                if (operations[index] == BfVm.OpOpen)
                {
                    loopEnds[index] = arguments[index];
                }
            }

            if (optimize)
            {
                Optimize();
            }
        }

        internal byte[] Operations
        {
            get { return operations; }
        }

        internal int[] Arguments
        {
            get { return arguments; }
        }

        public int Length
        {
            get { return operations.Length; }
        }

        public int IdiomCount
        {
            get { return idioms.Count; }
        }

        public int MulAddCount
        {
            get { return mulAddOffsets.Count; }
        }

        public IDictionary<string, int> IdiomsByName()
        {
            var counts = new Dictionary<string, int>();
            foreach (BfIdioms.Match match in idioms)
            {
                int count;
                counts.TryGetValue(match.Idiom.Name, out count);
                counts[match.Idiom.Name] = count + 1;
            }

            return counts;
        }

        internal int LoopEnd(int open)
        {
            return loopEnds[open];
        }

        internal BfIdioms.Match Idiom(int index)
        {
            return idioms[index];
        }

        // cells[p] is the non-zero loop counter.
        internal void MulAdd(int index, int[] cells, int p, int pc)
        {
            int[] offsets = mulAddOffsets[index];
            int[] factors = mulAddFactors[index];
            int step = factors[0];
            long count = cells[p];
            if ((step < 0) != (count > 0))
            {
                // Counting away from zero: plain execution would overflow.
                throw new BfFault("A Brainfuck cell overflowed.", pc);
            }

            if (count < 0)
            {
                count = -count;
            }

            for (int item = 1; item < offsets.Length; item++)
            {
                int target = p + offsets[item];
                if ((uint)target >= BfVm.TapeLength)
                {
                    throw new BfFault("The Brainfuck program moved outside its tape.", pc);
                }

                long value = cells[target] + (count * factors[item]);
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw new BfFault("A Brainfuck cell overflowed.", pc);
                }

                cells[target] = (int)value;
            }

            cells[p] = 0;
        }

        private void Optimize()
        {
            for (int open = 0; open < operations.Length; open++)
            {
                if (operations[open] != BfVm.OpOpen)
                {
                    continue;
                }

                int close = arguments[open];
                BfIdioms.Match match = BfIdioms.Recognize(operations, arguments, open, close);
                if (match != null)
                {
                    operations[open] = BfVm.OpIdiom;
                    arguments[open] = idioms.Count;
                    idioms.Add(match);
                    continue;
                }

                if (close == open + 2 && operations[open + 1] == BfVm.OpAdd && (arguments[open + 1] == 1 || arguments[open + 1] == -1))
                {
                    operations[open] = BfVm.OpClear;
                    arguments[open] = arguments[open + 1];
                    continue;
                }

                TryMulAdd(open, close);
            }
        }

        // [ only adds and moves, pointer back at start, counter changes by exactly one ]
        private void TryMulAdd(int open, int close)
        {
            var factors = new Dictionary<int, long>();
            int offset = 0;
            for (int index = open + 1; index < close; index++)
            {
                switch (operations[index])
                {
                    case BfVm.OpAdd:
                        long current;
                        factors.TryGetValue(offset, out current);
                        factors[offset] = current + arguments[index];
                        break;
                    case BfVm.OpMove:
                        offset += arguments[index];
                        break;
                    default:
                        return;
                }
            }

            long step;
            if (offset != 0 || !factors.TryGetValue(0, out step) || (step != 1 && step != -1))
            {
                return;
            }

            var offsets = new List<int> { 0 };
            var multipliers = new List<int> { (int)step };
            foreach (KeyValuePair<int, long> pair in factors)
            {
                if (pair.Key == 0 || pair.Value == 0)
                {
                    continue;
                }

                if (pair.Value < int.MinValue || pair.Value > int.MaxValue)
                {
                    return;
                }

                offsets.Add(pair.Key);
                // Counting up (+1) runs -count times: flip the factor so the
                // superinstruction can always use the absolute count.
                multipliers.Add((int)(step == -1 ? pair.Value : pair.Value));
            }

            operations[open] = BfVm.OpMulAdd;
            arguments[open] = mulAddOffsets.Count;
            mulAddOffsets.Add(offsets.ToArray());
            mulAddFactors.Add(multipliers.ToArray());
        }
    }
}
