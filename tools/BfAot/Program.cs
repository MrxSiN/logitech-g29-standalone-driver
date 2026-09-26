using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace G29.BfAot
{
    // Command line of the ahead-of-time Brainfuck compiler.
    //
    //   BfAot check <program.bf>
    //   BfAot strings <program.bf>     (constant text the program writes, with line:column)
    //   BfAot compile <output.c> [--stats <file>] [--table <name>] [--directory <dir>] <entry>=<program.bf> ...
    //
    // compile writes the output only when its content changes, so an unchanged
    // program does not trigger a rebuild of the generated object.
    internal static class Program
    {
        private static int Main(string[] arguments)
        {
            try
            {
                if (arguments.Length == 2 && arguments[0] == "check")
                {
                    var stats = new CompileStatistics();
                    AotCompiler.Load(File.ReadAllBytes(arguments[1]), stats);
                    Console.WriteLine("{0}: valid ({1} commands).", arguments[1], stats.Commands);
                    return 0;
                }

                if (arguments.Length == 2 && arguments[0] == "strings")
                {
                    byte[] source = File.ReadAllBytes(arguments[1]);
                    FlatProgram.Parse(source);
                    foreach (ConstantFrame frame in Strings(source))
                    {
                        Console.WriteLine(TextMap.Describe(frame));
                    }

                    return 0;
                }

                if (arguments.Length >= 3 && arguments[0] == "compile")
                {
                    return Compile(arguments);
                }

                Console.Error.WriteLine("Usage: BfAot check <program.bf> | BfAot compile <output-base> [--parts <n>] [--stats <file>] [--table <name>] [--directory <dir>] <entry>=<program.bf> ...");
                return 2;
            }
            catch (BfSourceException exception)
            {
                Console.Error.WriteLine("Brainfuck source error: " + exception.Message);
                return 1;
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        private static int Compile(string[] arguments)
        {
            string output = arguments[1];
            string statsPath = null, table = null;
            int threshold = AotCompiler.DefaultThreshold, parts = 1;
            var entries = new List<KeyValuePair<string, string>>();
            for (int index = 2; index < arguments.Length; index++)
            {
                if (arguments[index] == "--stats" && index + 1 < arguments.Length)
                {
                    statsPath = arguments[++index];
                }
                else if (arguments[index] == "--table" && index + 1 < arguments.Length)
                {
                    table = arguments[++index];
                }
                else if (arguments[index] == "--directory" && index + 1 < arguments.Length)
                {
                    // every <name>.bf in the directory becomes entry bfp_<name>
                    string[] files = Directory.GetFiles(arguments[++index], "*.bf");
                    Array.Sort(files, StringComparer.Ordinal);
                    foreach (string file in files)
                    {
                        if (!IsIdentifier(Path.GetFileNameWithoutExtension(file)))
                        {
                            Console.Error.WriteLine("'{0}' is not a valid program name.", file);
                            return 2;
                        }

                        entries.Add(new KeyValuePair<string, string>("bfp_" + Path.GetFileNameWithoutExtension(file), file));
                    }
                }
                else if (arguments[index] == "--parts" && index + 1 < arguments.Length)
                {
                    parts = Math.Max(1, int.Parse(arguments[++index], System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (arguments[index] == "--threshold" && index + 1 < arguments.Length)
                {
                    threshold = int.Parse(arguments[++index], System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    int split = arguments[index].IndexOf('=');
                    if (split <= 0 || !IsIdentifier(arguments[index].Substring(0, split)))
                    {
                        Console.Error.WriteLine("Expected <entry>=<program.bf>, got '{0}'.", arguments[index]);
                        return 2;
                    }

                    entries.Add(new KeyValuePair<string, string>(arguments[index].Substring(0, split), arguments[index].Substring(split + 1)));
                }
            }

            // A single program is identified by its content hash, so tests can check
            // that the shipping binaries were built from the committed source.
            string description = entries.Count == 1 ? Path.GetFileName(entries[0].Value) + " (SHA-256 " + Sha256(entries[0].Value) + ")" : "the test program corpus";
            var prototypes = new List<string>();
            var definitions = new List<string>();
            CompileStatistics first = null;
            foreach (KeyValuePair<string, string> entry in entries)
            {
                var stats = new CompileStatistics();
                List<Node> program;
                try
                {
                    program = AotCompiler.Load(File.ReadAllBytes(entry.Value), stats);
                }
                catch (BfSourceException exception)
                {
                    throw new BfSourceException(entry.Value + ": " + exception.Message);
                }

                CEmitter.Emit(program, entry.Key, threshold, stats, prototypes, definitions);
                first = first ?? stats;
            }

            if (table != null)
            {
                var text = new StringBuilder("const bf_named_program ").Append(table).Append("[] = {\n");
                foreach (KeyValuePair<string, string> entry in entries)
                {
                    text.Append(" { \"").Append(entry.Key).Append("\", ").Append(entry.Key).Append(" },\n");
                }

                definitions.Add(text.Append(" { 0, 0 }\n};\n").ToString());
            }

            // <base>.h declares every function; <base>_NN.c split the definitions
            // into parts of similar size so the C compiler can build them in parallel.
            string name = Path.GetFileName(output);
            var header = new StringBuilder(AotCompiler.Banner(description));
            header.Append("#ifndef ").Append(name.ToUpperInvariant()).Append("_H\n#define ").Append(name.ToUpperInvariant()).Append("_H\n#include \"bfrt.h\"\n");
            foreach (string prototype in prototypes)
            {
                header.Append(prototype).Append('\n');
            }

            long generatedBytes = WriteIfChanged(output + ".h", header.Append("#endif\n").ToString());
            long total = 0;
            foreach (string definition in definitions)
            {
                total += definition.Length;
            }

            var part = new StringBuilder();
            long written = 0;
            int partIndex = 0;
            foreach (string definition in definitions)
            {
                if (part.Length == 0)
                {
                    part.Append(AotCompiler.Banner(description)).Append("#include \"").Append(name).Append(".h\"\n\n");
                }

                part.Append(definition);
                written += definition.Length;
                if (partIndex < parts - 1 && written >= total * (partIndex + 1) / parts)
                {
                    generatedBytes += WriteIfChanged(PartPath(output, partIndex++), part.ToString());
                    part.Clear();
                }
            }

            while (partIndex < parts)
            {
                if (part.Length == 0)
                {
                    part.Append(AotCompiler.Banner(description)).Append("#include \"").Append(name).Append(".h\"\n");
                }

                generatedBytes += WriteIfChanged(PartPath(output, partIndex++), part.ToString());
                part.Clear();
            }

            if (statsPath != null && first != null)
            {
                first.GeneratedBytes = generatedBytes;
                File.WriteAllText(statsPath, first.Format());
            }

            return 0;
        }

        private static string Sha256(string path)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty);
            }
        }

        private static string PartPath(string output, int index)
        {
            return output + "_" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".c";
        }

        // Rewrites a file only when its content changes, so unchanged generated
        // code does not force the C compiler to run again. Returns the length.
        private static long WriteIfChanged(string path, string content)
        {
            if (!File.Exists(path) || File.ReadAllText(path) != content)
            {
                File.WriteAllText(path, content);
            }

            return content.Length;
        }

        // Constant text frames with their source line and column.
        private static List<ConstantFrame> Strings(byte[] source)
        {
            var commands = new StringBuilder(source.Length);
            var lines = new List<int>(source.Length);
            var columns = new List<int>(source.Length);
            int line = 1, column = 0;
            foreach (byte value in source)
            {
                column++;
                if (value == '\n')
                {
                    line++;
                    column = 0;
                }
                else if (value != '\r')
                {
                    commands.Append((char)value);
                    lines.Add(line);
                    columns.Add(column);
                }
            }

            List<ConstantFrame> frames = TextMap.Find(commands.ToString());
            foreach (ConstantFrame frame in frames)
            {
                frame.Line = lines[frame.Start];
                frame.Column = columns[frame.Start];
            }

            return frames;
        }

        private static bool IsIdentifier(string name)
        {
            if (name.Length == 0 || char.IsDigit(name[0]))
            {
                return false;
            }

            foreach (char c in name)
            {
                if (!(c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
