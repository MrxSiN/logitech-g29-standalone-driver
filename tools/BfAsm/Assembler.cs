using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BfAsm
{
    // Expands Brainfuck assembly into classic Brainfuck. Generic: it knows nothing
    // about what the program does. It resolves cell names to pointer moves, expands
    // macros, repeats code at assembly time, allocates scratch cells, and refuses
    // loops that do not return the data pointer to where they started.
    internal sealed class Assembler
    {
        private const int LineWidth = 72;
        private const int MaximumDepth = 120;

        private readonly StringBuilder output = new StringBuilder();
        private readonly Scope globals = new Scope(null);
        private readonly Dictionary<string, MacroNode> macros = new Dictionary<string, MacroNode>();
        private readonly List<Pool> pools = new List<Pool>();
        private readonly HashSet<string> included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> macroUse = new Dictionary<string, int>();
        private readonly List<string> map = new List<string>();

        // The data pointer: an absolute cell when known, otherwise an offset from an
        // anonymous anchor created by an unbalanced loop.
        private bool known = true;
        private long pointer;
        private int anchor;
        private int anchors;
        private int column;
        private int depth;
        private long maximumCell;
        private long commandCount;

        internal string Output
        {
            get { return column > 0 ? output.ToString() + "\n" : output.ToString(); }
        }

        internal long CommandCount
        {
            get { return commandCount; }
        }

        internal long MaximumCell
        {
            get { return maximumCell; }
        }

        internal IList<string> Map
        {
            get { return map; }
        }

        internal void Header(string text)
        {
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                Comment(line);
            }
        }

        // Macro expansion recurses deeply; run it on a thread with a large stack.
        internal void AssembleFile(string path)
        {
            Exception failure = null;
            var worker = new System.Threading.Thread(
                delegate()
                {
                    try
                    {
                        AssembleInclude(path);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                },
                256 * 1024 * 1024);
            worker.Start();
            worker.Join();
            if (failure != null)
            {
                var asm = failure as AsmException;
                throw asm != null ? new AsmException(asm.Message) : new AsmException("internal assembler error: " + failure);
            }
        }

        private void AssembleInclude(string path)
        {
            string full = Path.GetFullPath(path);
            if (!included.Add(full))
            {
                return;
            }

            string text = File.ReadAllText(full);
            List<Node> items = new Parser(Lexer.Tokenize(text, RelativeName(full))).ParseFile();
            string directory = Path.GetDirectoryName(full);
            foreach (Node item in items)
            {
                var include = item as IncludeNode;
                if (include != null)
                {
                    AssembleInclude(Path.Combine(directory, include.Path));
                    continue;
                }

                if (Declare(item))
                {
                    continue;
                }

                Expand(item, globals);
            }
        }

        private static string RelativeName(string full)
        {
            return Path.GetFileName(full);
        }

        private bool Declare(Node item)
        {
            var constant = item as ConstNode;
            if (constant != null)
            {
                globals.Define(constant.Name, Evaluate(constant.Value, globals), constant.At);
                return true;
            }

            var cell = item as CellNode;
            if (cell != null)
            {
                long address = Number(Evaluate(cell.Address, globals), cell.At);
                long length = cell.Length == null ? 1 : Number(Evaluate(cell.Length, globals), cell.At);
                if (address < 0 || length < 1)
                {
                    throw new AsmException(cell.At.Where + ": invalid cell declaration");
                }

                globals.Define(cell.Name, address, cell.At);
                map.Add(string.Format("cell {0,6} {1,5} {2}", address, length, cell.Name));
                return true;
            }

            var scratch = item as ScratchNode;
            if (scratch != null)
            {
                long from = Number(Evaluate(scratch.From, globals), scratch.At);
                long to = Number(Evaluate(scratch.To, globals), scratch.At);
                if (from < 0 || to <= from)
                {
                    throw new AsmException(scratch.At.Where + ": invalid scratch range");
                }

                foreach (Pool pool in pools)
                {
                    if (from < pool.End && pool.Start < to)
                    {
                        throw new AsmException(scratch.At.Where + ": scratch ranges overlap");
                    }
                }

                pools.Add(new Pool(from, to));
                map.Add(string.Format("scratch {0,6} {1,5}", from, to - from));
                return true;
            }

            var macro = item as MacroNode;
            if (macro != null)
            {
                if (macros.ContainsKey(macro.Name))
                {
                    throw new AsmException(macro.At.Where + ": macro '" + macro.Name + "' is already defined");
                }

                macros.Add(macro.Name, macro);
                return true;
            }

            return false;
        }

        private void ExpandAll(List<Node> nodes, Scope scope)
        {
            foreach (Node node in nodes)
            {
                Expand(node, scope);
            }
        }

        private void Expand(Node node, Scope scope)
        {
            var command = node as CommandNode;
            if (command != null)
            {
                Emit(command.Command, 1);
                return;
            }

            var repeat = node as RepeatNode;
            if (repeat != null)
            {
                long count = Number(Evaluate(repeat.Count, scope), repeat.At);
                if (count < 0)
                {
                    throw new AsmException(repeat.At.Where + ": negative repeat count " + count);
                }

                Emit(repeat.Command, count);
                return;
            }

            var go = node as GotoNode;
            if (go != null)
            {
                MoveTo(Number(Evaluate(go.Cell, scope), go.At), go.At);
                return;
            }

            var loop = node as LoopNode;
            if (loop != null)
            {
                bool startKnown = known;
                long startPointer = pointer;
                int startAnchor = anchor;
                Emit('[', 1);
                ExpandAll(loop.Body, new Scope(scope));
                if (loop.Unbalanced)
                {
                    known = false;
                    anchor = ++anchors;
                    pointer = 0;
                }
                else if (known != startKnown || pointer != startPointer || (!known && anchor != startAnchor))
                {
                    throw new AsmException(loop.At.Where + ": loop does not return the pointer to where it started (" + Describe(startKnown, startPointer) + " -> " + Describe(known, pointer) + ")");
                }

                Emit(']', 1);
                return;
            }

            var call = node as CallNode;
            if (call != null)
            {
                Call(call, scope);
                return;
            }

            var loopFor = node as ForNode;
            if (loopFor != null)
            {
                if (loopFor.Items != null)
                {
                    object items = Evaluate(loopFor.Items, scope);
                    var text = items as string;
                    if (text == null)
                    {
                        throw new AsmException(loopFor.At.Where + ": 'for ... in' needs a string");
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    for (int index = 0; index < bytes.Length; index++)
                    {
                        var inner = new Scope(scope);
                        inner.Define(loopFor.Variable, (long)bytes[index], loopFor.At);
                        if (loopFor.IndexVariable != null)
                        {
                            inner.Define(loopFor.IndexVariable, (long)index, loopFor.At);
                        }

                        ExpandAll(loopFor.Body, inner);
                    }

                    return;
                }

                long from = Number(Evaluate(loopFor.From, scope), loopFor.At);
                long to = Number(Evaluate(loopFor.To, scope), loopFor.At);
                for (long value = from; value <= to; value++)
                {
                    var inner = new Scope(scope);
                    inner.Define(loopFor.Variable, value, loopFor.At);
                    ExpandAll(loopFor.Body, inner);
                }

                return;
            }

            var branch = node as IfNode;
            if (branch != null)
            {
                if (Truth(Evaluate(branch.Condition, scope), branch.At))
                {
                    ExpandAll(branch.Then, new Scope(scope));
                }
                else if (branch.Otherwise != null)
                {
                    ExpandAll(branch.Otherwise, new Scope(scope));
                }

                return;
            }

            var local = node as LocalNode;
            if (local != null)
            {
                Local(local, scope);
                return;
            }

            var let = node as LetNode;
            if (let != null)
            {
                var inner = new Scope(scope);
                inner.Define(let.Name, Evaluate(let.Value, scope), let.At);
                ExpandAll(let.Body, inner);
                return;
            }

            var assume = node as AssumeNode;
            if (assume != null)
            {
                known = true;
                pointer = Number(Evaluate(assume.Cell, scope), assume.At);
                Track(pointer, assume.At);
                return;
            }

            var section = node as SectionNode;
            if (section != null)
            {
                object text = Evaluate(section.Text, scope);
                Comment(text as string ?? Convert.ToString(text));
                map.Add(string.Format("section {0,9} {1}", commandCount, text));
                return;
            }

            var assertion = node as AssertNode;
            if (assertion != null)
            {
                if (!Truth(Evaluate(assertion.Condition, scope), assertion.At))
                {
                    string message = assertion.Message == null ? "assertion failed" : Convert.ToString(Evaluate(assertion.Message, scope));
                    throw new AsmException(assertion.At.Where + ": " + message);
                }

                return;
            }

            var block = node as ScopeNode;
            if (block != null)
            {
                ExpandAll(block.Body, new Scope(scope));
                return;
            }

            if (node is ConstNode || node is CellNode || node is MacroNode || node is ScratchNode || node is IncludeNode)
            {
                throw new AsmException(node.At.Where + ": declarations are only allowed at the top level");
            }

            throw new AsmException(node.At.Where + ": unsupported construct");
        }

        private void Call(CallNode call, Scope scope)
        {
            object bound;
            if (scope.TryLookup(call.Name, out bound))
            {
                var block = bound as Block;
                if (block == null)
                {
                    throw new AsmException(call.At.Where + ": '" + call.Name + "' is not a macro or block");
                }

                if (call.Arguments.Count != 0)
                {
                    throw new AsmException(call.At.Where + ": a block takes no arguments");
                }

                Enter(call.At);
                ExpandAll(block.Body, new Scope(block.Scope));
                depth--;
                return;
            }

            MacroNode macro;
            if (!macros.TryGetValue(call.Name, out macro))
            {
                throw new AsmException(call.At.Where + ": unknown macro '" + call.Name + "'");
            }

            if (macro.Parameters.Count != call.Arguments.Count)
            {
                throw new AsmException(string.Format("{0}: macro '{1}' takes {2} argument(s), got {3}", call.At.Where, call.Name, macro.Parameters.Count, call.Arguments.Count));
            }

            var inner = new Scope(globals);
            for (int index = 0; index < macro.Parameters.Count; index++)
            {
                Expr argument = call.Arguments[index];
                var blockArgument = argument as BlockExpr;
                object value = blockArgument != null ? new Block(blockArgument.Body, scope) : Evaluate(argument, scope);
                inner.Define(macro.Parameters[index], value, call.At);
            }

            int uses;
            macroUse.TryGetValue(call.Name, out uses);
            macroUse[call.Name] = uses + 1;
            Enter(call.At);
            try
            {
                ExpandAll(macro.Body, inner);
            }
            catch (AsmException exception)
            {
                if (depth <= 1 || exception.Message.Contains("\n  expanded from"))
                {
                    throw new AsmException(exception.Message + "\n  expanded from " + call.At.Where + " (" + call.Name + ")");
                }

                throw;
            }

            depth--;
        }

        private void Enter(Token at)
        {
            if (++depth > MaximumDepth)
            {
                throw new AsmException(at.Where + ": macro expansion is nested too deeply (recursive macro?)");
            }
        }

        private void Local(LocalNode local, Scope scope)
        {
            var sizes = new long[local.Names.Count];
            long total = 0;
            for (int index = 0; index < sizes.Length; index++)
            {
                sizes[index] = local.Sizes[index] == null ? 1 : Number(Evaluate(local.Sizes[index], scope), local.At);
                if (sizes[index] < 1)
                {
                    throw new AsmException(local.At.Where + ": invalid local size");
                }

                total += sizes[index];
            }

            Pool pool = NearestPool(local.At);
            long start = pool.Top;
            if (start + total > pool.End)
            {
                throw new AsmException(string.Format("{0}: scratch pool {1}..{2} is exhausted", local.At.Where, pool.Start, pool.End));
            }

            pool.Top += total;
            if (pool.Top > pool.HighWater)
            {
                pool.HighWater = pool.Top;
            }

            var inner = new Scope(scope);
            long address = start;
            for (int index = 0; index < sizes.Length; index++)
            {
                inner.Define(local.Names[index], address, local.At);
                address += sizes[index];
            }

            ExpandAll(local.Body, inner);
            pool.Top -= total;
            if (pool.Top != start)
            {
                throw new AsmException(local.At.Where + ": scratch allocation is not nested");
            }
        }

        // Scratch cells come from the pool closest to the data pointer, so helpers
        // work next to the data they touch.
        private Pool NearestPool(Token at)
        {
            if (pools.Count == 0)
            {
                throw new AsmException(at.Where + ": no scratch pool is declared");
            }

            if (!known)
            {
                return pools[0];
            }

            Pool best = null;
            long bestDistance = long.MaxValue;
            foreach (Pool pool in pools)
            {
                if (pool.Top >= pool.End)
                {
                    continue;
                }

                long distance = Math.Abs(pool.Top - pointer);
                if (distance < bestDistance)
                {
                    best = pool;
                    bestDistance = distance;
                }
            }

            if (best == null)
            {
                throw new AsmException(at.Where + ": every scratch pool is exhausted");
            }

            return best;
        }

        private void MoveTo(long cell, Token at)
        {
            if (!known)
            {
                throw new AsmException(at.Where + ": the data pointer is unknown here (after an unbalanced loop); use 'assume @cell'");
            }

            if (cell < 0)
            {
                throw new AsmException(at.Where + ": negative cell address " + cell);
            }

            Emit(cell > pointer ? '>' : '<', Math.Abs(cell - pointer));
        }

        private void Track(long cell, Token at)
        {
            if (cell < 0)
            {
                throw new AsmException(at.Where + ": the data pointer would move below cell 0");
            }

            if (cell > maximumCell)
            {
                maximumCell = cell;
            }
        }

        private void Emit(char command, long count)
        {
            for (long index = 0; index < count; index++)
            {
                if (column == LineWidth)
                {
                    output.Append('\n');
                    column = 0;
                }

                output.Append(command);
                column++;
                commandCount++;
                if (command == '>')
                {
                    pointer++;
                    if (known && pointer > maximumCell)
                    {
                        maximumCell = pointer;
                    }
                }
                else if (command == '<')
                {
                    pointer--;
                    if (known && pointer < 0)
                    {
                        throw new AsmException("the data pointer moves below cell 0");
                    }
                }
            }
        }

        private void Comment(string text)
        {
            var clean = new StringBuilder();
            foreach (char c in text)
            {
                clean.Append("+-<>[].,".IndexOf(c) >= 0 || c == '\r' || c == '\n' ? ' ' : c);
            }

            if (column > 0)
            {
                output.Append('\n');
            }

            output.Append(clean.ToString().TrimEnd()).Append('\n');
            column = 0;
        }

        private static string Describe(bool isKnown, long cell)
        {
            return isKnown ? "cell " + cell : "unknown+" + cell;
        }

        internal IEnumerable<KeyValuePair<string, int>> MacroUse
        {
            get { return macroUse; }
        }

        internal IEnumerable<string> PoolReport
        {
            get
            {
                foreach (Pool pool in pools)
                {
                    yield return string.Format("scratch {0}..{1}: {2} of {3} cells used at peak", pool.Start, pool.End, pool.HighWater - pool.Start, pool.End - pool.Start);
                }
            }
        }

        // ---- expressions ----

        private object Evaluate(Expr expr, Scope scope)
        {
            var number = expr as NumberExpr;
            if (number != null)
            {
                return number.Value;
            }

            var text = expr as StringExpr;
            if (text != null)
            {
                return text.Value;
            }

            var name = expr as NameExpr;
            if (name != null)
            {
                object value;
                if (!scope.TryLookup(name.Name, out value))
                {
                    throw new AsmException(name.At.Where + ": unknown name '" + name.Name + "'");
                }

                return value;
            }

            var unary = expr as UnaryExpr;
            if (unary != null)
            {
                long operand = Number(Evaluate(unary.Operand, scope), unary.At);
                return unary.Op == "-" ? -operand : (operand == 0 ? 1L : 0L);
            }

            var binary = expr as BinaryExpr;
            if (binary != null)
            {
                return Binary(binary, scope);
            }

            var conditional = expr as ConditionalExpr;
            if (conditional != null)
            {
                return Truth(Evaluate(conditional.Condition, scope), conditional.At) ? Evaluate(conditional.Then, scope) : Evaluate(conditional.Otherwise, scope);
            }

            var function = expr as FunctionExpr;
            if (function != null)
            {
                return Function(function, scope);
            }

            if (expr is BlockExpr)
            {
                throw new AsmException(expr.At.Where + ": a block is only allowed as a macro argument");
            }

            throw new AsmException(expr.At.Where + ": unsupported expression");
        }

        private object Binary(BinaryExpr binary, Scope scope)
        {
            object leftValue = Evaluate(binary.Left, scope);
            if (binary.Op == "&&")
            {
                return Truth(leftValue, binary.At) && Truth(Evaluate(binary.Right, scope), binary.At) ? 1L : 0L;
            }

            if (binary.Op == "||")
            {
                return Truth(leftValue, binary.At) || Truth(Evaluate(binary.Right, scope), binary.At) ? 1L : 0L;
            }

            object rightValue = Evaluate(binary.Right, scope);
            if (leftValue is string || rightValue is string)
            {
                string leftText = leftValue as string;
                string rightText = rightValue as string;
                if (binary.Op == "+" && leftText != null && rightText != null)
                {
                    return leftText + rightText;
                }

                if ((binary.Op == "==" || binary.Op == "!=") && leftText != null && rightText != null)
                {
                    return (leftText == rightText) == (binary.Op == "==") ? 1L : 0L;
                }

                throw new AsmException(binary.At.Where + ": operator " + binary.Op + " does not apply to strings");
            }

            long left = Number(leftValue, binary.At);
            long right = Number(rightValue, binary.At);
            switch (binary.Op)
            {
                case "+":
                    return checked(left + right);
                case "-":
                    return checked(left - right);
                case "*":
                    return checked(left * right);
                case "/":
                case "%":
                    if (right == 0)
                    {
                        throw new AsmException(binary.At.Where + ": division by zero");
                    }

                    // Floor division, so negative values behave predictably.
                    long quotient = left / right;
                    if ((left % right != 0) && ((left < 0) != (right < 0)))
                    {
                        quotient--;
                    }

                    return binary.Op == "/" ? quotient : left - (quotient * right);
                case "==":
                    return left == right ? 1L : 0L;
                case "!=":
                    return left != right ? 1L : 0L;
                case "<":
                    return left < right ? 1L : 0L;
                case "<=":
                    return left <= right ? 1L : 0L;
                case ">":
                    return left > right ? 1L : 0L;
                case ">=":
                    return left >= right ? 1L : 0L;
            }

            throw new AsmException(binary.At.Where + ": unknown operator " + binary.Op);
        }

        private object Function(FunctionExpr function, Scope scope)
        {
            var arguments = new List<object>();
            foreach (Expr argument in function.Arguments)
            {
                arguments.Add(Evaluate(argument, scope));
            }

            switch (function.Name)
            {
                case "len":
                    Arity(function, arguments, 1);
                    return (long)Encoding.UTF8.GetByteCount(Text(arguments[0], function.At));
                case "byte":
                {
                    Arity(function, arguments, 2);
                    byte[] bytes = Encoding.UTF8.GetBytes(Text(arguments[0], function.At));
                    long index = Number(arguments[1], function.At);
                    if (index < 0 || index >= bytes.Length)
                    {
                        throw new AsmException(function.At.Where + ": byte index out of range");
                    }

                    return (long)bytes[index];
                }

                case "min":
                    Arity(function, arguments, 2);
                    return Math.Min(Number(arguments[0], function.At), Number(arguments[1], function.At));
                case "max":
                    Arity(function, arguments, 2);
                    return Math.Max(Number(arguments[0], function.At), Number(arguments[1], function.At));
                case "abs":
                    Arity(function, arguments, 1);
                    return Math.Abs(Number(arguments[0], function.At));
                case "isqrt":
                {
                    Arity(function, arguments, 1);
                    long value = Number(arguments[0], function.At);
                    if (value < 0)
                    {
                        throw new AsmException(function.At.Where + ": isqrt of a negative number");
                    }

                    long root = (long)Math.Sqrt(value);
                    while (root * root > value)
                    {
                        root--;
                    }

                    while ((root + 1) * (root + 1) <= value)
                    {
                        root++;
                    }

                    return root;
                }

                case "str":
                    Arity(function, arguments, 1);
                    return Convert.ToString(arguments[0], System.Globalization.CultureInfo.InvariantCulture);
                case "isblock":
                    Arity(function, arguments, 1);
                    return arguments[0] is Block ? 1L : 0L;
                case "isstring":
                    Arity(function, arguments, 1);
                    return arguments[0] is string ? 1L : 0L;
            }

            throw new AsmException(function.At.Where + ": unknown function '" + function.Name + "'");
        }

        private static void Arity(FunctionExpr function, List<object> arguments, int count)
        {
            if (arguments.Count != count)
            {
                throw new AsmException(function.At.Where + ": " + function.Name + " takes " + count + " argument(s)");
            }
        }

        private static long Number(object value, Token at)
        {
            if (value is long)
            {
                return (long)value;
            }

            throw new AsmException(at.Where + ": expected a number, found " + (value is string ? "a string" : value is Block ? "a block" : "nothing"));
        }

        private static string Text(object value, Token at)
        {
            var text = value as string;
            if (text == null)
            {
                throw new AsmException(at.Where + ": expected a string");
            }

            return text;
        }

        private static bool Truth(object value, Token at)
        {
            return Number(value, at) != 0;
        }

        private sealed class Pool
        {
            internal Pool(long start, long end)
            {
                Start = start;
                End = end;
                Top = start;
                HighWater = start;
            }

            internal long Start { get; private set; }

            internal long End { get; private set; }

            internal long Top { get; set; }

            internal long HighWater { get; set; }
        }

        private sealed class Block
        {
            internal Block(List<Node> body, Scope scope)
            {
                Body = body;
                Scope = scope;
            }

            internal List<Node> Body { get; private set; }

            internal Scope Scope { get; private set; }
        }

        private sealed class Scope
        {
            private readonly Scope parent;
            private readonly Dictionary<string, object> values = new Dictionary<string, object>();

            internal Scope(Scope parent)
            {
                this.parent = parent;
            }

            internal void Define(string name, object value, Token at)
            {
                if (values.ContainsKey(name))
                {
                    throw new AsmException(at.Where + ": '" + name + "' is already defined in this scope");
                }

                values.Add(name, value);
            }

            internal bool TryLookup(string name, out object value)
            {
                for (Scope scope = this; scope != null; scope = scope.parent)
                {
                    if (scope.values.TryGetValue(name, out value))
                    {
                        return true;
                    }
                }

                value = null;
                return false;
            }
        }
    }
}
