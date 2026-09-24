using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace G29.Tests
{
    // Reads the frozen legacy vectors in tests\reference (see reference.ps1).
    internal static class ReferenceFixtures
    {
        internal static string Directory
        {
            get
            {
                string current = AppDomain.CurrentDomain.BaseDirectory;
                while (current != null)
                {
                    string candidate = Path.Combine(current, Path.Combine("tests", "reference"));
                    if (System.IO.Directory.Exists(candidate))
                    {
                        return candidate;
                    }

                    current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
                }

                throw new InvalidOperationException("tests\\reference was not found above " + AppDomain.CurrentDomain.BaseDirectory);
            }
        }

        // Non-empty, non-comment lines.
        internal static IList<string> Lines(string name)
        {
            var lines = new List<string>();
            foreach (string line in File.ReadAllLines(Path.Combine(Directory, name)))
            {
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add(line);
            }

            if (lines.Count == 0)
            {
                throw new InvalidOperationException("Reference fixture " + name + " is empty.");
            }

            return lines;
        }

        internal static byte[] Hex(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = byte.Parse(text.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return bytes;
        }

        internal static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        internal static string Hex(IList<byte[]> reports)
        {
            var parts = new List<string>();
            foreach (byte[] report in reports)
            {
                parts.Add(Hex(report));
            }

            return string.Join(" ", parts.ToArray());
        }

        internal static long Long(string text)
        {
            return long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        internal static int HexInt(string text)
        {
            return int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
    }
}
