using System;
using System.Runtime.InteropServices;
using System.Text;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The native bridge's VM (src/bridge/common/bfvm.c, built as the test-only
    // artifacts\bin\g29testvm.dll) behind the harness's machine interface, so the
    // scenario suites can run the application program on it and on the C# VM.
    internal sealed class NativeVm : IBfMachine, IDisposable
    {
        private static IntPtr program = IntPtr.Zero;
        private static string programSource;

        private readonly ReadCallback readCallback;
        private readonly WriteCallback writeCallback;
        private readonly Func<int> read;
        private readonly Action<byte> write;
        private IntPtr vm;
        private Exception hostFailure;

        private NativeVm(Func<int> read, Action<byte> write, long stepBudget)
        {
            this.read = read;
            this.write = write;
            readCallback = Read;
            writeCallback = Write;
            vm = NativeMethods.bft_create(program, readCallback, writeCallback, stepBudget);
            if (vm == IntPtr.Zero)
            {
                throw new InvalidOperationException("The native VM could not be created.");
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadCallback();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int WriteCallback(int value);

        public long TotalSteps
        {
            get { return NativeMethods.bft_total_steps(vm); }
        }

        // Compiles the application program once; every harness then runs it natively.
        internal static void Use(string source)
        {
            if (program == IntPtr.Zero || !ReferenceEquals(source, programSource))
            {
                var error = new StringBuilder(256);
                byte[] bytes = Encoding.ASCII.GetBytes(source);
                program = NativeMethods.bft_compile(bytes, bytes.Length, error, error.Capacity);
                if (program == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The native VM refused the program: " + error);
                }

                programSource = source;
            }

            BfHarness.MachineFactory = delegate(BfProgram ignored, Func<int> read, Action<byte> write, long budget) { return new NativeVm(read, write, budget); };
        }

        internal static void Stop()
        {
            BfHarness.MachineFactory = null;
        }

        internal static int IdiomCount
        {
            get { return NativeMethods.bft_idiom_count(program); }
        }

        internal static int MulAddCount
        {
            get { return NativeMethods.bft_muladd_count(program); }
        }

        public bool Run()
        {
            var fault = new StringBuilder(256);
            int result = NativeMethods.bft_run(vm, fault, fault.Capacity);
            if (hostFailure != null)
            {
                Exception failure = hostFailure;
                hostFailure = null;
                throw failure;
            }

            if (result < 0)
            {
                throw new BfFault(fault.ToString(), -1);
            }

            return result == 1;
        }

        ~NativeVm()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (vm != IntPtr.Zero)
            {
                NativeMethods.bft_free(vm);
                vm = IntPtr.Zero;
            }
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

        private static class NativeMethods
        {
            private const string Library = "g29testvm.dll";

            [DllImport(Library)]
            internal static extern IntPtr bft_compile(byte[] source, int length, StringBuilder error, int errorLength);

            [DllImport(Library)]
            internal static extern int bft_idiom_count(IntPtr program);

            [DllImport(Library)]
            internal static extern int bft_muladd_count(IntPtr program);

            [DllImport(Library)]
            internal static extern IntPtr bft_create(IntPtr program, ReadCallback read, WriteCallback write, long stepBudget);

            [DllImport(Library)]
            internal static extern int bft_run(IntPtr vm, StringBuilder fault, int faultLength);

            [DllImport(Library)]
            internal static extern long bft_total_steps(IntPtr vm);

            [DllImport(Library)]
            internal static extern void bft_free(IntPtr vm);
        }
    }
}
