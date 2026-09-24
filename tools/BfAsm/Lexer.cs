using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BfAsm
{
    internal enum TokenKind
    {
        Identifier,
        Number,
        String,
        Punct,
        End
    }

    internal sealed class Token
    {
        internal Token(TokenKind kind, string text, long number, string file, int line, bool spaceBefore)
        {
            Kind = kind;
            Text = text;
            Number = number;
            File = file;
            Line = line;
            SpaceBefore = spaceBefore;
        }

        internal TokenKind Kind { get; private set; }

        internal string Text { get; private set; }

        internal long Number { get; private set; }

        internal string File { get; private set; }

        internal int Line { get; private set; }

        // True when whitespace, a comment or a line break separated this token from
        // the previous one; used to join two-character operators such as <= only when
        // they are written together.
        internal bool SpaceBefore { get; private set; }

        internal bool Is(string punct)
        {
            return Kind == TokenKind.Punct && Text == punct;
        }

        internal bool IsWord(string word)
        {
            return Kind == TokenKind.Identifier && Text == word;
        }

        internal string Where
        {
            get { return File + ":" + Line; }
        }

        public override string ToString()
        {
            return Kind == TokenKind.End ? "end of file" : "'" + Text + "'";
        }
    }

    // Comments run from ';' to the end of the line. Every Brainfuck command is a
    // single punctuation token, so the parser decides from context whether '+' is
    // an instruction or an operator.
    internal static class Lexer
    {
        internal static List<Token> Tokenize(string text, string file)
        {
            var tokens = new List<Token>();
            int line = 1;
            int index = 0;
            bool space = true;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '\n')
                {
                    line++;
                    index++;
                    space = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    index++;
                    space = true;
                    continue;
                }

                if (c == ';')
                {
                    while (index < text.Length && text[index] != '\n')
                    {
                        index++;
                    }

                    space = true;
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = index;
                    while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenKind.Identifier, text.Substring(start, index - start), 0, file, line, space));
                    space = false;
                    continue;
                }

                if (char.IsDigit(c))
                {
                    int start = index;
                    long value;
                    if (c == '0' && index + 1 < text.Length && (text[index + 1] == 'x' || text[index + 1] == 'X'))
                    {
                        index += 2;
                        int digits = index;
                        while (index < text.Length && Uri.IsHexDigit(text[index]))
                        {
                            index++;
                        }

                        if (index == digits)
                        {
                            throw new AsmException(file + ":" + line + ": malformed hexadecimal number");
                        }

                        value = long.Parse(text.Substring(digits, index - digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        while (index < text.Length && char.IsDigit(text[index]))
                        {
                            index++;
                        }

                        value = long.Parse(text.Substring(start, index - start), CultureInfo.InvariantCulture);
                    }

                    if (index < text.Length && (char.IsLetter(text[index]) || text[index] == '_'))
                    {
                        throw new AsmException(file + ":" + line + ": malformed number");
                    }

                    tokens.Add(new Token(TokenKind.Number, text.Substring(start, index - start), value, file, line, space));
                    space = false;
                    continue;
                }

                if (c == '"')
                {
                    var builder = new StringBuilder();
                    index++;
                    while (true)
                    {
                        if (index >= text.Length || text[index] == '\n')
                        {
                            throw new AsmException(file + ":" + line + ": unterminated string");
                        }

                        char s = text[index++];
                        if (s == '"')
                        {
                            break;
                        }

                        if (s == '\\')
                        {
                            if (index >= text.Length)
                            {
                                throw new AsmException(file + ":" + line + ": unterminated escape");
                            }

                            char e = text[index++];
                            switch (e)
                            {
                                case 'n':
                                    builder.Append('\n');
                                    break;
                                case 'r':
                                    builder.Append('\r');
                                    break;
                                case 't':
                                    builder.Append('\t');
                                    break;
                                case '0':
                                    builder.Append('\0');
                                    break;
                                case '\\':
                                case '"':
                                case '\'':
                                    builder.Append(e);
                                    break;
                                default:
                                    throw new AsmException(file + ":" + line + ": unknown escape \\" + e);
                            }

                            continue;
                        }

                        builder.Append(s);
                    }

                    tokens.Add(new Token(TokenKind.String, builder.ToString(), 0, file, line, space));
                    space = false;
                    continue;
                }

                if (c == '\'')
                {
                    if (index + 2 >= text.Length || text[index + 2] != '\'')
                    {
                        throw new AsmException(file + ":" + line + ": malformed character literal");
                    }

                    tokens.Add(new Token(TokenKind.Number, text.Substring(index, 3), text[index + 1], file, line, space));
                    index += 3;
                    space = false;
                    continue;
                }

                if ("+-<>[].,@*{}()=!&|/%?:#".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token(TokenKind.Punct, c.ToString(), 0, file, line, space));
                    index++;
                    space = false;
                    continue;
                }

                throw new AsmException(file + ":" + line + ": unexpected character '" + c + "'");
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, 0, file, line, true));
            return tokens;
        }
    }

    internal sealed class AsmException : Exception
    {
        internal AsmException(string message)
            : base(message)
        {
        }
    }
}
