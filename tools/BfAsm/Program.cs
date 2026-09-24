using System;
using System.IO;
using System.Linq;

namespace BfAsm
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2 || args.Length > 3)
            {
                Console.Error.WriteLine("Usage: bfasm <program.bfa> <output.bf> [<map.txt>]");
                return 1;
            }

            try
            {
                var assembler = new Assembler();
                assembler.AssembleFile(args[0]);
                File.WriteAllText(args[1], assembler.Output);
                if (args.Length == 3)
                {
                    var lines = assembler.Map.ToList();
                    lines.AddRange(assembler.PoolReport);
                    lines.AddRange(assembler.MacroUse.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => string.Format("macro {0,7} {1}", pair.Value, pair.Key)));
                    File.WriteAllText(args[2], string.Join("\n", lines.ToArray()) + "\n");
                }

                Console.WriteLine("THE ASSEMBLY HAS BEEN RENDERED INTO {0} BRAINFUCK COMMANDS ({1}); highest cell {2}", assembler.CommandCount, args[1], assembler.MaximumCell);
                return 0;
            }
            catch (AsmException exception)
            {
                Console.Error.WriteLine("bfasm: " + exception.Message);
                return 1;
            }
        }
    }
}
