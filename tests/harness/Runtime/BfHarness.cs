using System;
using System.Collections.Generic;

namespace G29.Bridge.Runtime
{
    // A machine the harness can drive: the reference interpreter (BfVm) or, in
    // the differential tests, the ahead-of-time compiled program (AotMachine).
    public interface IBfMachine
    {
        long TotalIterations { get; }

        // True when the program ended, false when read returned -1.
        bool Run();
    }

    // Deterministic single-threaded runner for tests: events are queued, the
    // program runs until it wants input that is not there, and every command frame
    // is recorded (and may be answered by a fake bridge).
    public sealed class BfHarness
    {
        // Creates the machine for a harness (program, read, write, iteration budget).
        // Tests replace it to run the same scenarios on another machine.
        public static Func<BfProgram, Func<int>, Action<byte>, long, IBfMachine> MachineFactory { get; set; }

        // Called for every new harness; the benchmark uses it to record workloads.
        public static Action<BfHarness> Created { get; set; }

        private readonly Queue<byte> input = new Queue<byte>();
        private readonly FrameParser parser = new FrameParser();
        private readonly List<Frame> outputs = new List<Frame>();
        private readonly List<byte> consumed = new List<byte>();
        private readonly List<byte> produced = new List<byte>();
        private readonly IBfMachine vm;

        public BfHarness(BfProgram program, long iterationBudget)
        {
            vm = MachineFactory != null ? MachineFactory(program, Read, Write, iterationBudget) : new BfVm(program, Read, Write, iterationBudget);
            if (Created != null)
            {
                Created(this);
            }
        }

        // Every input byte the program read and every byte it wrote, in order.
        public IList<byte> Consumed
        {
            get { return consumed; }
        }

        public IList<byte> Produced
        {
            get { return produced; }
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
            if (input.Count == 0)
            {
                return -1;
            }

            byte value = input.Dequeue();
            consumed.Add(value);
            return value;
        }

        private void Write(byte value)
        {
            produced.Add(value);
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
