using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace G29.Tests
{
    // Machine-checked architecture rules (AI_MAINTENANCE.md):
    //  - src/brainfuck/g29-main.bf is the only program source and holds nothing but
    //    the eight Brainfuck commands and line endings;
    //  - there is no higher-level source layer that generates Brainfuck, and no
    //    build step writes a .bf file;
    //  - the shipping binaries contain compiled code, not an interpreter or the
    //    program text.
    internal static class ArchitectureTests
    {
        private const string Commands = "><+-.,[]";

        internal static void Run()
        {
            RawBrainfuckOnly();
            NoGeneratingLayer();
            NoInterpreterInShippingBinaries();
            BuiltFromCommittedSource();
            BinaryArchitectures();
            Console.WriteLine("  architecture: raw Brainfuck source, no generating layer, no interpreter in the shipping binaries");
        }

        internal static string ProgramPath
        {
            get { return Path.Combine(BfMainTests.RepositoryRoot, "src", "brainfuck", "g29-main.bf"); }
        }

        private static void RawBrainfuckOnly()
        {
            byte[] source = File.ReadAllBytes(ProgramPath);
            Assert.True(source.Length > 0, "g29-main.bf is not empty");
            for (int index = 0; index < source.Length; index++)
            {
                byte value = source[index];
                if (value != '\r' && value != '\n' && Commands.IndexOf((char)value) < 0)
                {
                    throw new InvalidOperationException(string.Format("Assertion failed: g29-main.bf byte {0} is 0x{1:X2}; only {2} and line endings are allowed.", index, value, Commands));
                }
            }

            string[] programs = Directory.GetFiles(Path.Combine(BfMainTests.RepositoryRoot, "src", "brainfuck"), "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(programs.Length == 1 && Path.GetFileName(programs[0]) == "g29-main.bf", "src/brainfuck holds exactly one program, g29-main.bf, besides documentation");
        }

        // Every file of the repository that belongs to the sources (not build output).
        private static IEnumerable<string> RepositoryFiles()
        {
            string root = BfMainTests.RepositoryRoot;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string child in Directory.GetDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    if (name == ".git" || name == "artifacts" || name == ".claude" || name == "bin" || name == "obj")
                    {
                        continue;
                    }

                    pending.Push(child);
                }

                foreach (string file in Directory.GetFiles(directory))
                {
                    yield return file;
                }
            }
        }

        private static void NoGeneratingLayer()
        {
            // Names are assembled so this file does not match its own checks.
            string assemblyExtension = ".bf" + "a";
            string assemblerName = "Bf" + "Asm";
            var writers = new[] { "Set-Content", "Add-Content", "Out-File", "WriteAll", "Copy-Item", "Move-Item", "New-Item", "File.Create", "StreamWriter" };
            foreach (string file in RepositoryFiles())
            {
                string relative = file.Substring(BfMainTests.RepositoryRoot.Length + 1);
                Assert.False(file.EndsWith(assemblyExtension, StringComparison.OrdinalIgnoreCase), "no Brainfuck assembly source remains: " + relative);
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension != ".cs" && extension != ".ps1" && extension != ".c" && extension != ".h" && extension != ".yml")
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file);
                Assert.False(lines.Any(line => line.Contains(assemblerName) || line.Contains(assemblyExtension)), "no code refers to the retired Brainfuck assembler: " + relative);
                foreach (string line in lines)
                {
                    // A script or tool may read g29-main.bf, never write it.
                    if (line.Contains("g29-main.bf") && writers.Any(line.Contains))
                    {
                        throw new InvalidOperationException("Assertion failed: " + relative + " writes a Brainfuck program: " + line.Trim());
                    }
                }
            }

            Assert.False(Directory.Exists(Path.Combine(BfMainTests.RepositoryRoot, "tools", assemblerName)), "the assembler directory is gone");
            Assert.True(File.Exists(Path.Combine(BfMainTests.RepositoryRoot, "tools", "BfAot", "Compiler.cs")), "the ahead-of-time compiler exists");
        }

        private static IEnumerable<string> ShippingBinaries()
        {
            string bin = Path.Combine(BfMainTests.RepositoryRoot, "artifacts", "bin");
            return new[] { "g29ctl.exe", "g29ffb64.dll", "g29ffb32.dll" }.Select(name => Path.Combine(bin, name));
        }

        private static void NoInterpreterInShippingBinaries()
        {
            // The shipping sources contain no command dispatch over Brainfuck text.
            foreach (string file in Directory.GetFiles(Path.Combine(BfMainTests.RepositoryRoot, "src", "bridge"), "*.c", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "testhost" + Path.DirectorySeparatorChar))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                Assert.False(text.Contains("case '['") || text.Contains("case ']'") || text.Contains("case '+'"), "no Brainfuck command dispatch in " + Path.GetFileName(file));
            }

            Assert.False(File.Exists(Path.Combine(BfMainTests.RepositoryRoot, "src", "bridge", "common", "bfvm.c")), "the native interpreter source is gone");

            string[] retired =
            {
                "G29_PROGRAM",                                     // the embedded program resource
                "unmatched '['",                                   // run-time program parser
                "larger than the VM accepts",                      // run-time program parser
                "a[b[-f+g+b]g[-b+g]g+f[a-b-m+g-f[-]]g[a[-s+a]g-]a]" // run-time idiom matcher
            };
            foreach (string path in ShippingBinaries())
            {
                Assert.True(File.Exists(path), "the shipping binary exists (build.ps1): " + path);
                byte[] image = File.ReadAllBytes(path);
                string ascii = Encoding.ASCII.GetString(image);
                string wide = Encoding.Unicode.GetString(image) + Encoding.Unicode.GetString(image, 1, image.Length - 1);
                foreach (string text in retired)
                {
                    Assert.False(ascii.Contains(text) || wide.Contains(text), Path.GetFileName(path) + " does not contain '" + text + "'");
                }

                // Program text would show up as long runs of command characters.
                int run = 0, longest = 0;
                foreach (byte value in image)
                {
                    run = Commands.IndexOf((char)value) >= 0 ? run + 1 : 0;
                    longest = Math.Max(longest, run);
                }

                Assert.True(longest < 64, Path.GetFileName(path) + " carries no Brainfuck text (longest command-character run " + longest + ")");
            }
        }

        // The generated code the binaries were linked from names the SHA-256 of
        // the program it was compiled from.
        private static void BuiltFromCommittedSource()
        {
            string header = Path.Combine(BfMainTests.RepositoryRoot, "artifacts", "obj", "generated", "g29_program.h");
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(ProgramPath))).Replace("-", string.Empty);
            }

            Assert.True(File.ReadAllText(header).Contains("(SHA-256 " + hash + ")"), "the generated program was compiled from the committed g29-main.bf");
        }

        // g29ctl.exe and g29ffb64.dll are x64, g29ffb32.dll is x86.
        private static void BinaryArchitectures()
        {
            foreach (string path in ShippingBinaries())
            {
                byte[] image = File.ReadAllBytes(path);
                int header = BitConverter.ToInt32(image, 0x3C);
                Assert.True(image[header] == 'P' && image[header + 1] == 'E', Path.GetFileName(path) + " is a PE image");
                int machine = BitConverter.ToUInt16(image, header + 4);
                int expected = path.EndsWith("g29ffb32.dll", StringComparison.Ordinal) ? 0x014C : 0x8664;
                Assert.Equal(expected, machine, Path.GetFileName(path) + " machine type");
            }
        }
    }
}
