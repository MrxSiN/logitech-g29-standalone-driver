using System;
using System.IO;

namespace G29.Bridge.Runtime
{
    // Loads src/brainfuck/g29-main.bf, embedded in every build as a resource, and
    // compiles it once per process.
    public static class BrainfuckProgram
    {
        public const string ResourceName = "G29.Brainfuck.g29-main.bf";

        private static readonly object Sync = new object();
        private static BfProgram program;

        public static BfProgram Shared
        {
            get
            {
                lock (Sync)
                {
                    if (program == null)
                    {
                        program = new BfProgram(Source());
                    }

                    return program;
                }
            }
        }

        public static string Source()
        {
            using (Stream stream = typeof(BrainfuckProgram).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("The Brainfuck program is missing from this build. Rebuild with build.ps1.");
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
