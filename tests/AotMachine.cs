using System;
using System.Runtime.InteropServices;
using System.Text;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // A program compiled ahead of time by tools/BfAot (the application program or
    // an entry of the test corpus, in the test-only g29testhost.dll) behind the
    // harness's machine interface, so the scenario suites run on the shipping
    // code path and on the reference interpreter.
    internal sealed class AotMachine : IBfMachine, IDisposable
    {
        private readonly ReadCallback readCallback;
        private readonly WriteCallback writeCallback;
        private readonly Func<int> read;
        private readonly Action<byte> write;
        private IntPtr machine;
        private Exception hostFailure;

        internal AotMachine(IntPtr program, Func<int> read, Action<byte> write, long iterationBudget)
        {
            this.read = read;
            this.write = write;
            readCallback = Read;
            writeCallback = Write;
            machine = bft_create(program, readCallback, writeCallback, iterationBudget);
            if (machine == IntPtr.Zero)
            {
                throw new InvalidOperationException("The compiled program could not be started.");
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadCallback();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int WriteCallback(int value);

        public long TotalIterations
        {
            get { return bft_total_iterations(machine); }
        }

        // The most loop iterations any scenario ran between two input reads: the
        // measured basis of the iteration budget (bfrt.h).
        internal static long PeakIterations { get; private set; }

        internal string Fault { get; private set; }

        // A compiled program by name: "g29" or an entry of the test corpus.
        internal static IntPtr Program(string name)
        {
            IntPtr program = bft_program(name);
            if (program == IntPtr.Zero)
            {
                throw new InvalidOperationException("g29testhost.dll has no compiled program '" + name + "'; rebuild with test.ps1.");
            }

            return program;
        }

        // Every harness runs the compiled application program from now on.
        internal static void Use()
        {
            IntPtr program = Program("g29");
            BfHarness.MachineFactory = delegate(BfProgram ignored, Func<int> read, Action<byte> write, long budget) { return new AotMachine(program, read, write, budget); };
        }

        internal static void Stop()
        {
            BfHarness.MachineFactory = null;
        }

        // Preloads a cell before the first Run.
        internal void SetCell(int index, int value)
        {
            bft_set_cell(machine, index, value);
        }

        internal int Cell(int index)
        {
            return bft_cell(machine, index);
        }

        // 1 finished, 0 waiting for input, -1 fault (see Fault).
        internal int RunRaw()
        {
            var fault = new StringBuilder(256);
            int result = bft_run(machine, fault, fault.Capacity);
            PeakIterations = Math.Max(PeakIterations, bft_peak_iterations(machine));
            Fault = fault.ToString();
            if (hostFailure != null)
            {
                Exception failure = hostFailure;
                hostFailure = null;
                throw failure;
            }

            return result;
        }

        public bool Run()
        {
            int result = RunRaw();
            if (result < 0)
            {
                throw new BfFault(Fault, -1);
            }

            return result == 1;
        }

        ~AotMachine()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (machine != IntPtr.Zero)
            {
                bft_free(machine);
                machine = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }

        private int Read()
        {
            try
            {
                return read();
            }
            catch (Exception exception)
            {
                hostFailure = exception;
                return -1;
            }
        }

        private int Write(int value)
        {
            try
            {
                write((byte)value);
                return 0;
            }
            catch (Exception exception)
            {
                hostFailure = exception;
                return 1;
            }
        }

        private const string Library = "g29testhost.dll";

        [DllImport(Library, CharSet = CharSet.Ansi)]
        private static extern IntPtr bft_program(string name);

        [DllImport(Library)]
        private static extern IntPtr bft_create(IntPtr program, ReadCallback read, WriteCallback write, long iterationBudget);

        [DllImport(Library)]
        private static extern int bft_run(IntPtr machine, StringBuilder fault, int faultLength);

        [DllImport(Library)]
        private static extern long bft_total_iterations(IntPtr machine);

        [DllImport(Library)]
        private static extern long bft_peak_iterations(IntPtr machine);

        [DllImport(Library)]
        private static extern void bft_set_cell(IntPtr machine, int index, int value);

        [DllImport(Library)]
        private static extern int bft_cell(IntPtr machine, int index);

        [DllImport(Library)]
        private static extern void bft_free(IntPtr machine);
    }
}
