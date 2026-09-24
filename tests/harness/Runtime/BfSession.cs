using System;
using System.Collections.Generic;
using System.Threading;

namespace G29.Bridge.Runtime
{
    // One persistent Brainfuck program on its own thread. Producers post event
    // frames from any thread; the program consumes them strictly in order. Command
    // frames the program writes are handed to the bridge on the program's thread,
    // so the bridge may answer by posting result events.
    public sealed class BfSession : IDisposable
    {
        private readonly object sync = new object();
        private readonly Queue<byte[]> pending = new Queue<byte[]>();
        private readonly BfVm vm;
        private readonly FrameParser parser = new FrameParser();
        private readonly Action<Frame> onCommand;
        private readonly Action<Exception> onFailure;
        private readonly Thread thread;
        private byte[] current;
        private int currentIndex;
        private bool stopping;
        private bool finished;
        private Exception failure;

        public BfSession(BfProgram program, Action<Frame> onCommand, Action<Exception> onFailure, long stepBudget, string threadName)
        {
            if (onCommand == null)
            {
                throw new ArgumentNullException("onCommand");
            }

            this.onCommand = onCommand;
            this.onFailure = onFailure;
            vm = new BfVm(program, Read, Write, stepBudget);
            thread = new Thread(Run);
            thread.Name = threadName ?? "Brainfuck";
            thread.IsBackground = true;
        }

        public Exception Failure
        {
            get
            {
                lock (sync)
                {
                    return failure;
                }
            }
        }

        public bool IsAlive
        {
            get { return thread.IsAlive; }
        }

        public bool Finished
        {
            get
            {
                lock (sync)
                {
                    return finished;
                }
            }
        }

        public int PendingBytes
        {
            get
            {
                lock (sync)
                {
                    int count = current == null ? 0 : current.Length - currentIndex;
                    foreach (byte[] item in pending)
                    {
                        count += item.Length;
                    }

                    return count;
                }
            }
        }

        public void Start()
        {
            thread.Start();
        }

        public void Post(Frame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            byte[] bytes = frame.Encode();
            lock (sync)
            {
                if (stopping)
                {
                    return;
                }

                pending.Enqueue(bytes);
                Monitor.PulseAll(sync);
            }
        }

        // Makes the next blocking read return "shut down". The program keeps running
        // until it blocks for input.
        public void Stop()
        {
            lock (sync)
            {
                stopping = true;
                Monitor.PulseAll(sync);
            }
        }

        public bool Join(TimeSpan timeout)
        {
            return thread.Join(timeout);
        }

        public void Dispose()
        {
            Stop();
            Join(TimeSpan.FromSeconds(2));
        }

        private int Read()
        {
            lock (sync)
            {
                while (true)
                {
                    if (current != null && currentIndex < current.Length)
                    {
                        return current[currentIndex++];
                    }

                    if (pending.Count > 0)
                    {
                        current = pending.Dequeue();
                        currentIndex = 0;
                        continue;
                    }

                    if (stopping)
                    {
                        return -1;
                    }

                    Monitor.Wait(sync);
                }
            }
        }

        private void Write(byte value)
        {
            Frame frame = parser.Push(value);
            if (frame != null)
            {
                onCommand(frame);
            }
        }

        private void Run()
        {
            try
            {
                bool ended = vm.Run();
                lock (sync)
                {
                    finished = ended;
                }

                if (ended && !stopping)
                {
                    Fail(new BfFault("The Brainfuck program ended without being asked to.", -1));
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void Fail(Exception exception)
        {
            lock (sync)
            {
                failure = exception;
                stopping = true;
            }

            if (onFailure != null)
            {
                try
                {
                    onFailure(exception);
                }
                catch (Exception)
                {
                    // The failure path is best effort; nothing may escape this thread.
                }
            }
        }
    }

    // Deterministic single-threaded runner for tests: events are queued, the
    // program runs until it wants input that is not there, and every command frame
    // is recorded (and may be answered by a fake bridge).
    // A virtual machine the harness can drive: the C# BfVm, or (in tests) the
    // native bridge's VM.
    public interface IBfMachine
    {
        long TotalSteps { get; }

        // True when the program ended, false when read returned -1.
        bool Run();
    }

    public sealed class BfHarness
    {
        // Creates the machine for a harness (program, read, write, step budget).
        // Tests replace it to run the same scenarios on another VM.
        public static Func<BfProgram, Func<int>, Action<byte>, long, IBfMachine> MachineFactory { get; set; }

        private readonly Queue<byte> input = new Queue<byte>();
        private readonly FrameParser parser = new FrameParser();
        private readonly List<Frame> outputs = new List<Frame>();
        private readonly IBfMachine vm;

        public BfHarness(BfProgram program, long stepBudget)
        {
            vm = MachineFactory != null ? MachineFactory(program, Read, Write, stepBudget) : new BfVm(program, Read, Write, stepBudget);
        }

        // Called for every command frame; may Post answers.
        public Action<Frame> OnCommand { get; set; }

        public IList<Frame> Outputs
        {
            get { return outputs; }
        }

        public IBfMachine Machine
        {
            get { return vm; }
        }

        public bool Ended { get; private set; }

        public void Post(Frame frame)
        {
            PostBytes(frame.Encode());
        }

        public void PostBytes(byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                input.Enqueue(b);
            }
        }

        // Runs until the program blocks on empty input or ends. Returns the frames
        // produced during this call.
        public IList<Frame> Run()
        {
            int before = outputs.Count;
            if (!Ended)
            {
                Ended = vm.Run();
            }

            return outputs.GetRange(before, outputs.Count - before);
        }

        public bool MidFrame
        {
            get { return parser.InFrame; }
        }

        private int Read()
        {
            return input.Count == 0 ? -1 : input.Dequeue();
        }

        private void Write(byte value)
        {
            Frame frame = parser.Push(value);
            if (frame != null)
            {
                outputs.Add(frame);
                if (OnCommand != null)
                {
                    OnCommand(frame);
                }
            }
        }
    }
}
