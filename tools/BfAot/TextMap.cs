using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace G29.BfAot
{
    // One command frame whose every byte is a constant in the program: the
    // program writes it with the fragment
    //   +{b0}.[-] +{b1}.[-] ... (header and fixed fields, one cell t)
    //   +/-{d}. per text byte (the difference to the previous byte), then [-]
    // on a single cell that is zero before and after, with the pointer staying
    // on that cell. Found by pattern, not by any other source of information.
    public sealed class ConstantFrame
    {
        public int Start;
        public int End;
        public int Line;
        public int Column;
        public byte Type;
        public byte[] Prefix;
        public string Text;
    }

    // Diagnostic map of the constant text the program writes (console, log and
    // event-log frames), derived from the raw program. Read-only: nothing is
    // generated from it.
    public static class TextMap
    {
        // Payload bytes before the text length for each text-carrying command.
        private static readonly Dictionary<byte, int> PrefixLength = new Dictionary<byte, int>
        {
            { 0x81, 1 }, // CMD_LOG: severity
            { 0x82, 1 }, // CMD_CONSOLE_WRITE: stream
            { 0x83, 1 }, // CMD_EVENTLOG_WRITE: severity
            { 0x85, 0 }  // CMD_TEXT_APPEND
        };

        // commands: the program without line endings. lines: for each command
        // index, its line (1-based) in the source; columns likewise.
        public static List<ConstantFrame> Find(string commands)
        {
            var frames = new List<ConstantFrame>();
            for (int start = 0; start < commands.Length; start++)
            {
                char c = commands[start];
                if ((c != '+' && c != '>') || (start > 0 && (commands[start - 1] == '+' || commands[start - 1] == '-')))
                {
                    continue;
                }

                ConstantFrame frame = Parse(commands, start);
                if (frame != null)
                {
                    frames.Add(frame);
                    start = frame.End - 1;
                }
            }
            return frames;
        }

        private static ConstantFrame Parse(string s, int start)
        {
            int position = start;
            var header = new int[8];
            for (int index = 0; index < 8; index++)
            {
                header[index] = Constant(s, ref position);
                if (header[index] < 0 || (index == 0 && header[0] != 0xA5))
                {
                    return null;
                }
            }

            int prefixLength;
            if (header[0] != 0xA5 || header[1] != 1 || header[3] != 0 || !PrefixLength.TryGetValue((byte)header[2], out prefixLength))
            {
                return null;
            }

            var prefix = new byte[prefixLength];
            for (int index = 0; index < prefixLength; index++)
            {
                int value = Constant(s, ref position);
                if (value < 0)
                {
                    return null;
                }

                prefix[index] = (byte)value;
            }

            int low = Constant(s, ref position), high = Constant(s, ref position);
            if (low < 0 || high < 0)
            {
                return null;
            }

            int length = low + 256 * high;
            if (header[6] + 256 * header[7] != prefixLength + 2 + length || length == 0)
            {
                return null;
            }

            var text = new StringBuilder();
            int value2 = 0;
            for (int index = 0; index < length; index++)
            {
                int delta;
                if (!Add(s, ref position, out delta) || position >= s.Length || s[position] != '.')
                {
                    return null;
                }

                position++;
                value2 += delta;
                if (value2 < 0 || value2 > 255)
                {
                    return null;
                }

                text.Append((char)value2);
            }

            if (string.CompareOrdinal(s, position, "[-]", 0, 3) != 0)
            {
                return null;
            }

            return new ConstantFrame { Start = start, End = position + 3, Type = (byte)header[2], Prefix = prefix, Text = text.ToString() };
        }

        // One constant byte: an addition to the zero cell t, then .[-]
        private static int Constant(string s, ref int position)
        {
            int k, p = position;
            if (!Add(s, ref p, out k) || k < 0 || k > 255 || string.CompareOrdinal(s, p, ".[-]", 0, 4) != 0)
            {
                return -1;
            }

            position = p + 4;
            return k;
        }

        // An addition of a constant to cell t, written as runs of + or -, and
        // for larger constants as a product using the cell t+1:
        // >+{a}[-<+{b}>]<  or  >[-]+{a}[-<+{b}>]<  (or -{b}) adds a*b (or -a*b).
        private static bool Add(string s, ref int position, out int delta)
        {
            delta = 0;
            int p = position;
            while (p < s.Length)
            {
                if (s[p] == '+' || s[p] == '-')
                {
                    delta += s[p] == '+' ? 1 : -1;
                    p++;
                    continue;
                }

                if (s[p] != '>')
                {
                    break;
                }

                int q = p + 1, a = 0, b = 0;
                if (string.CompareOrdinal(s, q, "[-]", 0, 3) == 0)
                {
                    q += 3;
                }

                while (q < s.Length && s[q] == '+')
                {
                    a++;
                    q++;
                }

                if (a == 0 || string.CompareOrdinal(s, q, "[-<", 0, 3) != 0)
                {
                    break;
                }

                q += 3;
                char sign = q < s.Length ? s[q] : ' ';
                while (q < s.Length && s[q] == sign && (sign == '+' || sign == '-'))
                {
                    b++;
                    q++;
                }

                if (b == 0 || string.CompareOrdinal(s, q, ">]<", 0, 3) != 0)
                {
                    break;
                }

                delta += (sign == '+' ? 1 : -1) * a * b;
                p = q + 3;
            }

            position = p;
            return true;
        }

        // The fragment that writes the frame (the inverse of Parse): used by
        // maintainers to replace a message; the result is plain Brainfuck.
        public static string Fragment(byte type, byte[] prefix, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int payload = prefix.Length + 2 + bytes.Length;
            var result = new StringBuilder();
            foreach (int value in new[] { 0xA5, 1, type, 0, 0, 0, payload % 256, payload / 256 })
            {
                result.Append('+', value).Append(".[-]");
            }

            foreach (byte value in prefix)
            {
                result.Append('+', value).Append(".[-]");
            }

            result.Append('+', bytes.Length % 256).Append(".[-]").Append('+', bytes.Length / 256).Append(".[-]");
            int previous = 0;
            foreach (byte value in bytes)
            {
                result.Append(value >= previous ? '+' : '-', Math.Abs(value - previous)).Append('.');
                previous = value;
            }

            return result.Append("[-]").ToString();
        }

        // Every place the program writes exactly this text with consecutive
        // outputs of one cell (additions, '.' per byte, then [-]). Returns the
        // start of the first addition and the index after the [-].
        public static List<KeyValuePair<int, int>> FindText(string commands, string text)
        {
            var found = new List<KeyValuePair<int, int>>();
            for (int start = 0; start < commands.Length; start++)
            {
                char c = commands[start];
                if ((c != '+' && c != '>') || (start > 0 && (commands[start - 1] == '+' || commands[start - 1] == '-')))
                {
                    continue;
                }

                int position = start, value = 0, index = 0;
                for (; index < text.Length; index++)
                {
                    int delta;
                    Add(commands, ref position, out delta);
                    value += delta;
                    if (value != text[index] || position >= commands.Length || commands[position] != '.')
                    {
                        break;
                    }

                    position++;
                }

                if (index == text.Length && string.CompareOrdinal(commands, position, "[-]", 0, 3) == 0)
                {
                    found.Add(new KeyValuePair<int, int>(start, position + 3));
                }
            }

            return found;
        }

        public static string Describe(ConstantFrame frame)
        {
            string escaped = frame.Text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1} 0x{2:X2} [{3}] \"{4}\"", frame.Line, frame.Column, frame.Type, BitConverter.ToString(frame.Prefix), escaped);
        }
    }
}
