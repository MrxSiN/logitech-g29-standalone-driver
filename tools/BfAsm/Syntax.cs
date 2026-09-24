using System.Collections.Generic;

namespace BfAsm
{
    internal abstract class Node
    {
        protected Node(Token at)
        {
            At = at;
        }

        internal Token At { get; private set; }
    }

    // ---- code ----

    internal sealed class CommandNode : Node
    {
        internal CommandNode(Token at, char command)
            : base(at)
        {
            Command = command;
        }

        internal char Command { get; private set; }
    }

    internal sealed class RepeatNode : Node
    {
        internal RepeatNode(Token at, char command, Expr count)
            : base(at)
        {
            Command = command;
            Count = count;
        }

        internal char Command { get; private set; }

        internal Expr Count { get; private set; }
    }

    internal sealed class GotoNode : Node
    {
        internal GotoNode(Token at, Expr cell)
            : base(at)
        {
            Cell = cell;
        }

        internal Expr Cell { get; private set; }
    }

    internal sealed class LoopNode : Node
    {
        internal LoopNode(Token at, bool unbalanced, List<Node> body)
            : base(at)
        {
            Unbalanced = unbalanced;
            Body = body;
        }

        internal bool Unbalanced { get; private set; }

        internal List<Node> Body { get; private set; }
    }

    internal sealed class CallNode : Node
    {
        internal CallNode(Token at, string name, List<Expr> arguments)
            : base(at)
        {
            Name = name;
            Arguments = arguments;
        }

        internal string Name { get; private set; }

        internal List<Expr> Arguments { get; private set; }
    }

    internal sealed class ForNode : Node
    {
        internal ForNode(Token at, string variable, string indexVariable, Expr from, Expr to, Expr items, List<Node> body)
            : base(at)
        {
            Variable = variable;
            IndexVariable = indexVariable;
            From = from;
            To = to;
            Items = items;
            Body = body;
        }

        internal string Variable { get; private set; }

        internal string IndexVariable { get; private set; }

        internal Expr From { get; private set; }

        internal Expr To { get; private set; }

        // Non-null for 'for c in "string"'.
        internal Expr Items { get; private set; }

        internal List<Node> Body { get; private set; }
    }

    internal sealed class IfNode : Node
    {
        internal IfNode(Token at, Expr condition, List<Node> then, List<Node> otherwise)
            : base(at)
        {
            Condition = condition;
            Then = then;
            Otherwise = otherwise;
        }

        internal Expr Condition { get; private set; }

        internal List<Node> Then { get; private set; }

        internal List<Node> Otherwise { get; private set; }
    }

    internal sealed class LocalNode : Node
    {
        internal LocalNode(Token at, List<string> names, List<Expr> sizes, List<Node> body)
            : base(at)
        {
            Names = names;
            Sizes = sizes;
            Body = body;
        }

        internal List<string> Names { get; private set; }

        internal List<Expr> Sizes { get; private set; }

        internal List<Node> Body { get; private set; }
    }

    internal sealed class LetNode : Node
    {
        internal LetNode(Token at, string name, Expr value, List<Node> body)
            : base(at)
        {
            Name = name;
            Value = value;
            Body = body;
        }

        internal string Name { get; private set; }

        internal Expr Value { get; private set; }

        internal List<Node> Body { get; private set; }
    }

    internal sealed class AssumeNode : Node
    {
        internal AssumeNode(Token at, Expr cell)
            : base(at)
        {
            Cell = cell;
        }

        internal Expr Cell { get; private set; }
    }

    internal sealed class SectionNode : Node
    {
        internal SectionNode(Token at, Expr text)
            : base(at)
        {
            Text = text;
        }

        internal Expr Text { get; private set; }
    }

    internal sealed class AssertNode : Node
    {
        internal AssertNode(Token at, Expr condition, Expr message)
            : base(at)
        {
            Condition = condition;
            Message = message;
        }

        internal Expr Condition { get; private set; }

        internal Expr Message { get; private set; }
    }

    internal sealed class ScopeNode : Node
    {
        internal ScopeNode(Token at, List<Node> body)
            : base(at)
        {
            Body = body;
        }

        internal List<Node> Body { get; private set; }
    }

    // ---- declarations (top level only) ----

    internal sealed class ConstNode : Node
    {
        internal ConstNode(Token at, string name, Expr value)
            : base(at)
        {
            Name = name;
            Value = value;
        }

        internal string Name { get; private set; }

        internal Expr Value { get; private set; }
    }

    internal sealed class CellNode : Node
    {
        internal CellNode(Token at, string name, Expr length, Expr address)
            : base(at)
        {
            Name = name;
            Length = length;
            Address = address;
        }

        internal string Name { get; private set; }

        internal Expr Length { get; private set; }

        internal Expr Address { get; private set; }
    }

    internal sealed class ScratchNode : Node
    {
        internal ScratchNode(Token at, Expr from, Expr to)
            : base(at)
        {
            From = from;
            To = to;
        }

        internal Expr From { get; private set; }

        internal Expr To { get; private set; }
    }

    internal sealed class MacroNode : Node
    {
        internal MacroNode(Token at, string name, List<string> parameters, List<Node> body)
            : base(at)
        {
            Name = name;
            Parameters = parameters;
            Body = body;
        }

        internal string Name { get; private set; }

        internal List<string> Parameters { get; private set; }

        internal List<Node> Body { get; private set; }
    }

    // ---- expressions ----

    internal abstract class Expr
    {
        protected Expr(Token at)
        {
            At = at;
        }

        internal Token At { get; private set; }
    }

    internal sealed class NumberExpr : Expr
    {
        internal NumberExpr(Token at, long value)
            : base(at)
        {
            Value = value;
        }

        internal long Value { get; private set; }
    }

    internal sealed class StringExpr : Expr
    {
        internal StringExpr(Token at, string value)
            : base(at)
        {
            Value = value;
        }

        internal string Value { get; private set; }
    }

    internal sealed class NameExpr : Expr
    {
        internal NameExpr(Token at, string name)
            : base(at)
        {
            Name = name;
        }

        internal string Name { get; private set; }
    }

    internal sealed class IndexExpr : Expr
    {
        internal IndexExpr(Token at, Expr target, Expr index)
            : base(at)
        {
            Target = target;
            Index = index;
        }

        internal Expr Target { get; private set; }

        internal Expr Index { get; private set; }
    }

    internal sealed class UnaryExpr : Expr
    {
        internal UnaryExpr(Token at, string op, Expr operand)
            : base(at)
        {
            Op = op;
            Operand = operand;
        }

        internal string Op { get; private set; }

        internal Expr Operand { get; private set; }
    }

    internal sealed class BinaryExpr : Expr
    {
        internal BinaryExpr(Token at, string op, Expr left, Expr right)
            : base(at)
        {
            Op = op;
            Left = left;
            Right = right;
        }

        internal string Op { get; private set; }

        internal Expr Left { get; private set; }

        internal Expr Right { get; private set; }
    }

    internal sealed class ConditionalExpr : Expr
    {
        internal ConditionalExpr(Token at, Expr condition, Expr then, Expr otherwise)
            : base(at)
        {
            Condition = condition;
            Then = then;
            Otherwise = otherwise;
        }

        internal Expr Condition { get; private set; }

        internal Expr Then { get; private set; }

        internal Expr Otherwise { get; private set; }
    }

    internal sealed class FunctionExpr : Expr
    {
        internal FunctionExpr(Token at, string name, List<Expr> arguments)
            : base(at)
        {
            Name = name;
            Arguments = arguments;
        }

        internal string Name { get; private set; }

        internal List<Expr> Arguments { get; private set; }
    }

    internal sealed class BlockExpr : Expr
    {
        internal BlockExpr(Token at, List<Node> body)
            : base(at)
        {
            Body = body;
        }

        internal List<Node> Body { get; private set; }
    }
}
