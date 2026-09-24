using System.Collections.Generic;

namespace BfAsm
{
    // Brainfuck assembly grammar (see tools/BfAsm/README.md):
    //   top    := 'include' STRING | 'const' NAME '=' expr | 'cell' NAME ['[' expr ']'] '=' expr
    //           | 'scratch' expr 'to' expr | 'macro' NAME '(' names ')' block | code
    //   code   := + - < > , . | ('+'|'-'|'<'|'>') '*' primary | '@' primary
    //           | '[' ['!'] code ']' | NAME '(' args ')' {block} | block
    //           | 'for' NAME '=' expr 'to' expr block | 'for' NAME [',' NAME] 'in' expr block
    //           | 'if' expr block ['else' (block | if)] | 'local' NAME ['[' expr ']'] {',' ...} block
    //           | 'let' NAME '=' expr block | 'assume' '@' primary | 'section' expr | 'assert' expr [',' expr]
    internal sealed class Parser
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "include", "const", "cell", "scratch", "macro", "for", "in", "to", "if", "else", "local", "let", "assume", "section", "assert"
        };

        private readonly List<Token> tokens;
        private int position;

        internal Parser(List<Token> tokens)
        {
            this.tokens = tokens;
        }

        private Token Current
        {
            get { return tokens[position]; }
        }

        internal List<Node> ParseFile()
        {
            var items = new List<Node>();
            while (Current.Kind != TokenKind.End)
            {
                Token at = Current;
                if (at.IsWord("include"))
                {
                    Next();
                    Token path = Expect(TokenKind.String, "include path");
                    items.Add(new IncludeNode(at, path.Text));
                }
                else if (at.IsWord("const"))
                {
                    Next();
                    string name = Name();
                    ExpectPunct("=");
                    items.Add(new ConstNode(at, name, ParseLineExpression(at)));
                }
                else if (at.IsWord("cell"))
                {
                    Next();
                    string name = Name();
                    Expr length = null;
                    if (Current.Is("["))
                    {
                        Next();
                        length = ParseExpression();
                        ExpectPunct("]");
                    }

                    ExpectPunct("=");
                    items.Add(new CellNode(at, name, length, ParseLineExpression(at)));
                }
                else if (at.IsWord("scratch"))
                {
                    Next();
                    Expr from = ParseExpression();
                    ExpectWord("to");
                    items.Add(new ScratchNode(at, from, ParseLineExpression(at)));
                }
                else if (at.IsWord("macro"))
                {
                    Next();
                    string name = Name();
                    ExpectPunct("(");
                    var parameters = new List<string>();
                    if (!Current.Is(")"))
                    {
                        parameters.Add(Name());
                        while (Current.Is(","))
                        {
                            Next();
                            parameters.Add(Name());
                        }
                    }

                    ExpectPunct(")");
                    items.Add(new MacroNode(at, name, parameters, ParseBlock()));
                }
                else
                {
                    items.Add(ParseCodeItem());
                }
            }

            return items;
        }

        private List<Node> ParseBlock()
        {
            ExpectPunct("{");
            var body = new List<Node>();
            while (!Current.Is("}"))
            {
                if (Current.Kind == TokenKind.End)
                {
                    throw Error("unterminated block");
                }

                body.Add(ParseCodeItem());
            }

            Next();
            return body;
        }

        private Node ParseCodeItem()
        {
            Token at = Current;
            if (at.Kind == TokenKind.Punct)
            {
                switch (at.Text)
                {
                    case "+":
                    case "-":
                    case "<":
                    case ">":
                        Next();
                        if (Current.Is("*") && !Current.SpaceBefore)
                        {
                            Next();
                            return new RepeatNode(at, at.Text[0], ParsePrimary());
                        }

                        return new CommandNode(at, at.Text[0]);
                    case ",":
                    case ".":
                        Next();
                        return new CommandNode(at, at.Text[0]);
                    case "@":
                        Next();
                        return new GotoNode(at, ParsePrimary());
                    case "[":
                    {
                        Next();
                        bool unbalanced = false;
                        if (Current.Is("!") && !Current.SpaceBefore)
                        {
                            Next();
                            unbalanced = true;
                        }

                        var body = new List<Node>();
                        while (!Current.Is("]"))
                        {
                            if (Current.Kind == TokenKind.End || Current.Is("}"))
                            {
                                throw new AsmException(at.Where + ": unmatched '['");
                            }

                            body.Add(ParseCodeItem());
                        }

                        Next();
                        return new LoopNode(at, unbalanced, body);
                    }

                    case "]":
                        throw Error("unmatched ']'");
                    case "{":
                        return new ScopeNode(at, ParseBlock());
                }

                throw Error("unexpected " + at);
            }

            if (at.Kind != TokenKind.Identifier)
            {
                throw Error("expected code, found " + at);
            }

            switch (at.Text)
            {
                case "for":
                {
                    Next();
                    string variable = Name();
                    if (Current.Is("="))
                    {
                        Next();
                        Expr from = ParseExpression();
                        ExpectWord("to");
                        Expr to = ParseExpression();
                        return new ForNode(at, variable, null, from, to, null, ParseBlock());
                    }

                    string index = null;
                    if (Current.Is(","))
                    {
                        Next();
                        index = Name();
                    }

                    ExpectWord("in");
                    Expr items = ParseExpression();
                    return new ForNode(at, variable, index, null, null, items, ParseBlock());
                }

                case "if":
                    return ParseIf();
                case "local":
                {
                    Next();
                    var names = new List<string>();
                    var sizes = new List<Expr>();
                    do
                    {
                        if (names.Count > 0)
                        {
                            Next();
                        }

                        names.Add(Name());
                        if (Current.Is("[") && !Current.SpaceBefore)
                        {
                            Next();
                            sizes.Add(ParseExpression());
                            ExpectPunct("]");
                        }
                        else
                        {
                            sizes.Add(null);
                        }
                    }
                    while (Current.Is(","));

                    return new LocalNode(at, names, sizes, ParseBlock());
                }

                case "let":
                {
                    Next();
                    string name = Name();
                    ExpectPunct("=");
                    Expr value = ParseExpression();
                    return new LetNode(at, name, value, ParseBlock());
                }

                case "assume":
                    Next();
                    ExpectPunct("@");
                    return new AssumeNode(at, ParsePrimary());
                case "section":
                    Next();
                    return new SectionNode(at, ParseExpression());
                case "assert":
                {
                    Next();
                    Expr condition = ParseExpression();
                    Expr message = null;
                    if (Current.Is(","))
                    {
                        Next();
                        message = ParseExpression();
                    }

                    return new AssertNode(at, condition, message);
                }
            }

            if (Keywords.Contains(at.Text))
            {
                throw Error("'" + at.Text + "' is not allowed here");
            }

            Next();
            ExpectPunct("(");
            List<Expr> arguments = ParseArguments();
            while (Current.Is("{"))
            {
                Token blockAt = Current;
                arguments.Add(new BlockExpr(blockAt, ParseBlock()));
            }

            return new CallNode(at, at.Text, arguments);
        }

        private Node ParseIf()
        {
            Token at = Current;
            Next();
            Expr condition = ParseExpression();
            List<Node> then = ParseBlock();
            List<Node> otherwise = null;
            if (Current.IsWord("else"))
            {
                Next();
                if (Current.IsWord("if"))
                {
                    otherwise = new List<Node> { ParseIf() };
                }
                else
                {
                    otherwise = ParseBlock();
                }
            }

            return new IfNode(at, condition, then, otherwise);
        }

        // Arguments after '(' up to and including ')'.
        private List<Expr> ParseArguments()
        {
            var arguments = new List<Expr>();
            if (Current.Is(")"))
            {
                Next();
                return arguments;
            }

            while (true)
            {
                if (Current.Is("{"))
                {
                    Token at = Current;
                    arguments.Add(new BlockExpr(at, ParseBlock()));
                }
                else
                {
                    arguments.Add(ParseExpression());
                }

                if (Current.Is(")"))
                {
                    Next();
                    return arguments;
                }

                ExpectPunct(",");
            }
        }

        // A declaration's expression ends at the end of its line.
        private Expr ParseLineExpression(Token declaration)
        {
            int line = Current.Line;
            string file = Current.File;
            int start = position;
            int depth = 0;
            int end = position;
            while (tokens[end].Kind != TokenKind.End && (tokens[end].Line == line && tokens[end].File == file || depth > 0))
            {
                if (tokens[end].Is("("))
                {
                    depth++;
                }
                else if (tokens[end].Is(")"))
                {
                    depth--;
                }

                end++;
            }

            if (end == start)
            {
                throw new AsmException(declaration.Where + ": missing value");
            }

            var slice = tokens.GetRange(start, end - start);
            slice.Add(new Token(TokenKind.End, string.Empty, 0, file, line, true));
            var inner = new Parser(slice);
            Expr value = inner.ParseExpression();
            if (inner.Current.Kind != TokenKind.End)
            {
                throw new AsmException(inner.Current.Where + ": unexpected " + inner.Current + " in declaration");
            }

            position = end;
            return value;
        }

        internal Expr ParseExpression()
        {
            Expr condition = ParseBinary(0);
            if (Current.Is("?"))
            {
                Token at = Current;
                Next();
                Expr then = ParseExpression();
                ExpectPunct(":");
                Expr otherwise = ParseExpression();
                return new ConditionalExpr(at, condition, then, otherwise);
            }

            return condition;
        }

        private static readonly string[][] Levels =
        {
            new[] { "||" },
            new[] { "&&" },
            new[] { "==", "!=" },
            new[] { "<", "<=", ">", ">=" },
            new[] { "+", "-" },
            new[] { "*", "/", "%" }
        };

        private Expr ParseBinary(int level)
        {
            if (level == Levels.Length)
            {
                return ParseUnary();
            }

            Expr left = ParseBinary(level + 1);
            while (true)
            {
                Token at = Current;
                string op = PeekOperator();
                if (op == null || System.Array.IndexOf(Levels[level], op) < 0)
                {
                    return left;
                }

                position += op.Length;
                left = new BinaryExpr(at, op, left, ParseBinary(level + 1));
            }
        }

        // Joins two adjacent punctuation tokens into one operator.
        private string PeekOperator()
        {
            if (Current.Kind != TokenKind.Punct)
            {
                return null;
            }

            Token next = tokens[position + 1];
            string first = Current.Text;
            if (next.Kind == TokenKind.Punct && !next.SpaceBefore)
            {
                string pair = first + next.Text;
                if (pair == "==" || pair == "!=" || pair == "<=" || pair == ">=" || pair == "&&" || pair == "||")
                {
                    return pair;
                }
            }

            if (first == "=" || first == "!" || first == "&" || first == "|")
            {
                return null;
            }

            return first;
        }

        private Expr ParseUnary()
        {
            Token at = Current;
            if (at.Is("-") || at.Is("!"))
            {
                Next();
                return new UnaryExpr(at, at.Text, ParseUnary());
            }

            return ParsePrimary();
        }

        private Expr ParsePrimary()
        {
            Token at = Current;
            Expr result;
            switch (at.Kind)
            {
                case TokenKind.Number:
                    Next();
                    result = new NumberExpr(at, at.Number);
                    break;
                case TokenKind.String:
                    Next();
                    result = new StringExpr(at, at.Text);
                    break;
                case TokenKind.Identifier:
                    if (Keywords.Contains(at.Text))
                    {
                        throw Error("unexpected keyword '" + at.Text + "' in expression");
                    }

                    Next();
                    if (Current.Is("(") && !Current.SpaceBefore)
                    {
                        Next();
                        result = new FunctionExpr(at, at.Text, ParseArguments());
                    }
                    else
                    {
                        result = new NameExpr(at, at.Text);
                    }

                    break;
                case TokenKind.Punct:
                    if (at.Is("("))
                    {
                        Next();
                        result = ParseExpression();
                        ExpectPunct(")");
                        break;
                    }

                    if (at.Is("-"))
                    {
                        Next();
                        result = new UnaryExpr(at, "-", ParsePrimary());
                        break;
                    }

                    throw Error("expected a value, found " + at);
                default:
                    throw Error("expected a value, found " + at);
            }

            // No postfix indexing: '@x[-]' must stay "go to x, then clear loop".
            // Array elements are addressed as @(buffer + index).
            return result;
        }

        private string Name()
        {
            Token token = Expect(TokenKind.Identifier, "name");
            if (Keywords.Contains(token.Text))
            {
                throw new AsmException(token.Where + ": '" + token.Text + "' is a keyword");
            }

            return token.Text;
        }

        private Token Expect(TokenKind kind, string what)
        {
            if (Current.Kind != kind)
            {
                throw Error("expected " + what + ", found " + Current);
            }

            Token token = Current;
            Next();
            return token;
        }

        private void ExpectPunct(string punct)
        {
            if (!Current.Is(punct))
            {
                throw Error("expected '" + punct + "', found " + Current);
            }

            Next();
        }

        private void ExpectWord(string word)
        {
            if (!Current.IsWord(word))
            {
                throw Error("expected '" + word + "', found " + Current);
            }

            Next();
        }

        private void Next()
        {
            if (Current.Kind != TokenKind.End)
            {
                position++;
            }
        }

        private AsmException Error(string message)
        {
            return new AsmException(Current.Where + ": " + message);
        }
    }

    internal sealed class IncludeNode : Node
    {
        internal IncludeNode(Token at, string path)
            : base(at)
        {
            Path = path;
        }

        internal string Path { get; private set; }
    }
}
