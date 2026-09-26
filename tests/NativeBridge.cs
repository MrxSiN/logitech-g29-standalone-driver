using System;
using System.Runtime.InteropServices;

namespace G29.Tests
{
    // The native bridge's emergency guard and shared memory
    // (src/bridge/common/guard.c, system.c) through the test-only g29testhost.dll.
    internal sealed class NativeGuard : IDisposable
    {
        private readonly GuardWriter writer;
        private IntPtr guard;

        // write(path, output length, payload) returns 0 or a Win32 error.
        internal NativeGuard(int ceilingPercent, Func<string, int, byte[], int> write)
        {
            writer = delegate(string path, int length, IntPtr payload, int count)
            {
                var bytes = new byte[count];
                Marshal.Copy(payload, bytes, 0, count);
                return write(path, length, bytes);
            };
            guard = bft_guard_create(ceilingPercent, writer);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int GuardWriter(string path, int outputLength, IntPtr payload, int length);

        internal bool AnyForce
        {
            get { return bft_guard_any(guard) != 0; }
        }

        // True when the guard refuses the payload.
        internal bool Refuses(byte[] payload)
        {
            return bft_guard_check(guard, payload, payload.Length) != 0;
        }

        internal void Written(string path, int outputLength, byte[] payload)
        {
            bft_guard_written(guard, path, outputLength, payload, payload.Length);
        }

        internal void EmergencyStop()
        {
            bft_guard_stop(guard);
        }

        internal IntPtr Handle
        {
            get { return guard; }
        }

        // 0, or -1 when the guard refuses (a raise or an impossible value).
        internal int Lower(int ceilingPercent)
        {
            return bft_guard_lower(guard, ceilingPercent);
        }

        internal void Trip()
        {
            bft_guard_trip(guard);
        }

        // Replaces the guard's clock with a fixed time (ms).
        internal void FakeClock(long now)
        {
            bft_guard_fake_clock(guard, now);
        }

        internal long HeldMs(long now)
        {
            return bft_guard_held(guard, now);
        }

        // lease_evaluate: 0 holds, 1 hold limit, 2 stalled program.
        internal int Evaluate(IntPtr session, int maxHoldMs, int stallMs, long now)
        {
            return bft_lease_evaluate(guard, session, maxHoldMs, stallMs, now);
        }

        public void Dispose()
        {
            if (guard != IntPtr.Zero)
            {
                bft_guard_free(guard);
                guard = IntPtr.Zero;
            }
        }

        [DllImport("g29testhost.dll")]
        private static extern IntPtr bft_guard_create(int ceilingPercent, GuardWriter writer);

        [DllImport("g29testhost.dll")]
        private static extern int bft_guard_check(IntPtr guard, byte[] payload, int length);

        [DllImport("g29testhost.dll", CharSet = CharSet.Unicode)]
        private static extern void bft_guard_written(IntPtr guard, string path, int outputLength, byte[] payload, int length);

        [DllImport("g29testhost.dll")]
        private static extern int bft_guard_any(IntPtr guard);

        [DllImport("g29testhost.dll")]
        private static extern void bft_guard_stop(IntPtr guard);

        [DllImport("g29testhost.dll")]
        private static extern void bft_guard_free(IntPtr guard);

        [DllImport("g29testhost.dll")]
        private static extern int bft_guard_lower(IntPtr guard, int ceilingPercent);

        [DllImport("g29testhost.dll")]
        private static extern void bft_guard_trip(IntPtr guard);

        [DllImport("g29testhost.dll")]
        private static extern void bft_guard_fake_clock(IntPtr guard, long now);

        [DllImport("g29testhost.dll")]
        private static extern long bft_guard_held(IntPtr guard, long now);

        [DllImport("g29testhost.dll")]
        private static extern int bft_lease_evaluate(IntPtr guard, IntPtr session, int maxHoldMs, int stallMs, long now);
    }

    internal sealed class NativeSharedMemory : IDisposable
    {
        private IntPtr table = bft_shm_table();

        internal int Create(string name, int size, bool everyone, out uint handle)
        {
            return bft_shm_create(table, name, size, everyone ? 1 : 0, out handle);
        }

        internal int Open(string name, int size, out uint handle)
        {
            return bft_shm_open(table, name, size, out handle);
        }

        // Null when the range lies outside the mapping or the handle is unknown.
        internal byte[] Read(uint handle, int offset, int length)
        {
            var data = new byte[length];
            return bft_shm_read(table, handle, offset, data, length) == 0 ? data : null;
        }

        internal bool Write(uint handle, int offset, byte[] data)
        {
            return bft_shm_write(table, handle, offset, data, data.Length) == 0;
        }

        internal void Close(uint handle)
        {
            bft_shm_close(table, handle);
        }

        public void Dispose()
        {
            if (table != IntPtr.Zero)
            {
                bft_shm_free(table);
                table = IntPtr.Zero;
            }
        }

        [DllImport("g29testhost.dll")]
        private static extern IntPtr bft_shm_table();

        [DllImport("g29testhost.dll")]
        private static extern void bft_shm_free(IntPtr table);

        [DllImport("g29testhost.dll", CharSet = CharSet.Unicode)]
        private static extern int bft_shm_create(IntPtr table, string name, int size, int everyone, out uint handle);

        [DllImport("g29testhost.dll", CharSet = CharSet.Unicode)]
        private static extern int bft_shm_open(IntPtr table, string name, int size, out uint handle);

        [DllImport("g29testhost.dll")]
        private static extern int bft_shm_read(IntPtr table, uint handle, int offset, byte[] data, int length);

        [DllImport("g29testhost.dll")]
        private static extern int bft_shm_write(IntPtr table, uint handle, int offset, byte[] data, int length);

        [DllImport("g29testhost.dll")]
        private static extern void bft_shm_close(IntPtr table, uint handle);
    }
}
