using System;
using System.Collections.Generic;
using System.Text;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // A deterministic bridge for the Brainfuck program: scripted HID devices, a
    // virtual clock for timers, and a log in the fixture vocabulary
    // (open/send/close/open-failed/send-failed).
    internal sealed class FakeBridge
    {
        private readonly List<Phase> phases = new List<Phase>();
        private readonly Dictionary<uint, FakeDevice> tokens = new Dictionary<uint, FakeDevice>();
        private readonly StringBuilder stdout = new StringBuilder();
        private readonly StringBuilder stderr = new StringBuilder();
        private readonly List<string> log = new List<string>();
        private BfHarness harness;
        private uint nextToken = 1;
        private int current;
        private long clock = 5000000000L;
        private string pendingText = string.Empty;
        private string consoleLine = string.Empty;
        private int ticks;
        private readonly Dictionary<uint, string> sharedHandles = new Dictionary<uint, string>();
        private uint nextSharedHandle = 1;
        private bool watchdogArmed;

        internal FakeBridge(params Phase[] phases)
        {
            this.phases.AddRange(phases);
            PhaseAfterNativeSwitch = -1;
            // Every enumeration also reports unrelated HID interfaces; the program
            // must ignore them.
            Unrelated = new List<FakeDevice>
            {
                new FakeDevice("kbd", "USB Keyboard", 0x045E, 0x07A5, 0x0100, 0x01, 0x06, 9, 2),
                new FakeDevice("g27", "Logitech G27 Racing Wheel", 0x046D, 0xC29B, 0x1230, 0x01, 0x04, 12, 7),
                new FakeDevice("vendor", "Logitech G29 vendor collection", 0x046D, 0xC24F, 0x8900, 0xFF00, 0x01, 20, 20),
                new FakeDevice("other", "Other vendor wheel", 0x044F, 0xC24F, 0x8900, 0x01, 0x04, 13, 17)
            };
        }

        internal int PhaseAfterNativeSwitch { get; set; }

        internal string FailOpenPath { get; set; }

        internal string FailSendPath { get; set; }

        internal List<FakeDevice> Unrelated { get; private set; }

        internal IList<string> Log
        {
            get { return log; }
        }

        internal string Stdout
        {
            get { return stdout.ToString(); }
        }

        internal string Stderr
        {
            get { return stderr.ToString(); }
        }

        internal long? ExitCode { get; private set; }

        internal int? Legacy { get; private set; }

        internal int Enumerations { get; private set; }

        internal long Clock
        {
            get { return clock; }
        }

        // Boots role CLI with the arguments and runs until exit (or until the
        // program waits with nothing left to deliver).
        internal void RunCli(string[] arguments)
        {
            harness = new BfHarness(BfMainTests.Program, BfVm.DefaultIterationBudget);
            harness.OnCommand = Handle;
            harness.Post(BfMainTests.Boot(1, arguments));
            harness.Run();
        }

        // Delivers an extra event (for example Ctrl+C) and keeps running.
        internal void Post(Frame frame)
        {
            harness.Post(frame);
            harness.Run();
        }

        internal bool Ended
        {
            get { return harness.Ended; }
        }

        internal FakeRegistry Registry = new FakeRegistry();

        internal List<KeyValuePair<int, string>> Processes = new List<KeyValuePair<int, string>>();

        internal Dictionary<string, string[]> Files = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        internal string HostDirectory = @"C:\Program Files\G29Standalone\";

        internal int RegistryRequests { get; private set; }

        // Watch/service tests: console lines go into the log as "log <line>".
        internal bool LogConsoleLines { get; set; }

        // Monitor tests: after this many one-second monitor waits ("tick"), deliver
        // StopEvent instead of the timer; every earlier tick advances one phase.
        internal int? StopAfterTicks { get; set; }

        internal byte StopEvent { get; set; }

        // Timer id whose events are held back (the timer "never fires" in the test).
        internal int? HoldTimer { get; set; }

        // Named shared memory (CMD_SHM_*): contents by name; a test may preset a
        // mapping before the program creates or opens it.
        internal Dictionary<string, byte[]> SharedMemory = new Dictionary<string, byte[]>();

        internal int SharedMemoryCreateError { get; set; }

        // The system tick count (ms) the fake reports with every timer event.
        internal long TickMilliseconds
        {
            get { return clock / 1000; }
        }

        private void Handle(Frame frame)
        {
            var reader = new PayloadReader(frame.Payload);
            switch (frame.Type)
            {
                case 0x80:
                    ExitCode = reader.U32();
                    reader.End();
                    break;
                case 0x81:
                    reader.U8();
                    log.Add("log " + reader.Text());
                    reader.End();
                    break;
                case 0x82:
                {
                    int stream = reader.U8();
                    string text = reader.Text();
                    reader.End();
                    (stream == 2 ? stderr : stdout).Append(text);
                    if (LogConsoleLines)
                    {
                        consoleLine += text;
                        int end;
                        while ((end = consoleLine.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
                        {
                            log.Add("log " + consoleLine.Substring(0, end));
                            consoleLine = consoleLine.Substring(end + 2);
                        }
                    }

                    break;
                }

                case 0x84:
                    log.Add(string.Format("abi-error {0} {1:X2}", reader.U8(), reader.U8()));
                    break;
                case 0x90:
                    reader.End();
                    Enumerate(frame.Sequence);
                    break;
                case 0x91:
                {
                    FakeDevice device = Device(reader.U32());
                    reader.End();
                    log.Add("open " + device.Path);
                    int error = 0;
                    if (device.Path == FailOpenPath)
                    {
                        log.Add("open-failed " + device.Path);
                        error = 5;
                    }

                    harness.Post(new Frame(0x13, 0, frame.Sequence, new PayloadWriter().U32(device.Token).U32(error).ToArray()));
                    break;
                }

                case 0x92:
                {
                    FakeDevice device = Device(reader.U32());
                    reader.End();
                    log.Add("close " + device.Path);
                    break;
                }

                case 0x93:
                {
                    FakeDevice device = Device(reader.U32());
                    int length = reader.U16();
                    byte[] payload = reader.Bytes(length);
                    reader.End();
                    log.Add("send " + device.Path + " " + ReferenceFixtures.Hex(payload));
                    int error = 0;
                    if (device.Path == FailSendPath)
                    {
                        log.Add("send-failed " + device.Path);
                        error = 31;
                    }
                    else if (payload.Length == 7 && payload[0] == 0xF8 && payload[1] == 0x09 && PhaseAfterNativeSwitch >= 0)
                    {
                        current = PhaseAfterNativeSwitch;
                    }

                    harness.Post(new Frame(0x14, 0, frame.Sequence, new PayloadWriter().U32(device.Token).U32(error).ToArray()));
                    break;
                }

                case 0xA0:
                {
                    int timer = reader.U8();
                    reader.U8();
                    long interval = reader.U32();
                    reader.End();
                    log.Add("timer " + timer + " " + interval);
                    if (timer == 3)
                    {
                        // the service watchdog's repeating check: one tick is
                        // delivered before every one-second monitor tick
                        watchdogArmed = true;
                        break;
                    }

                    if (timer == 2 && interval == 1000 && StopAfterTicks.HasValue)
                    {
                        log.Add("tick");
                        ticks++;
                        if (ticks >= StopAfterTicks.Value)
                        {
                            harness.Post(new Frame(StopEvent, 0, 0, new byte[0]));
                            break;
                        }

                        if (current < phases.Count - 1)
                        {
                            current++;
                        }
                    }

                    if (HoldTimer == timer)
                    {
                        break;
                    }

                    clock += interval * 1000;
                    if (watchdogArmed && timer == 2 && interval == 1000)
                    {
                        harness.Post(new Frame(0x02, 0, 0, new PayloadWriter().U8(3).U64(clock).U64(TickMilliseconds).ToArray()));
                    }

                    harness.Post(new Frame(0x02, 0, 0, new PayloadWriter().U8(timer).U64(clock).U64(TickMilliseconds).ToArray()));
                    break;
                }

                case 0xA1:
                {
                    int timer = reader.U8();
                    reader.End();
                    log.Add("timer-cancel " + timer);
                    if (timer == 3)
                    {
                        watchdogArmed = false;
                    }

                    break;
                }

                case 0xC8:
                {
                    string name = reader.Text();
                    long size = reader.U32();
                    int everyone = reader.U8();
                    reader.End();
                    log.Add("shm-create " + name + " " + size + " " + everyone);
                    if (SharedMemoryCreateError != 0)
                    {
                        harness.Post(new Frame(0x33, 0, frame.Sequence, new PayloadWriter().U32(SharedMemoryCreateError).U32(0).ToArray()));
                        break;
                    }

                    if (!SharedMemory.ContainsKey(name))
                    {
                        SharedMemory[name] = new byte[size];
                    }

                    uint handle = nextSharedHandle++;
                    sharedHandles[handle] = name;
                    harness.Post(new Frame(0x33, 0, frame.Sequence, new PayloadWriter().U32(0).U32(handle).ToArray()));
                    break;
                }

                case 0xCA:
                {
                    byte[] memory = SharedMemory[sharedHandles[(uint)reader.U32()]];
                    int offset = (int)reader.U32();
                    int length = reader.U16();
                    reader.End();
                    var data = new byte[length];
                    Array.Copy(memory, offset, data, 0, length);
                    harness.Post(new Frame(0x33, 0, frame.Sequence, new PayloadWriter().U32(0).Bytes(data).ToArray()));
                    break;
                }

                case 0xCB:
                {
                    byte[] memory = SharedMemory[sharedHandles[(uint)reader.U32()]];
                    int offset = (int)reader.U32();
                    byte[] data = reader.Bytes(reader.U16());
                    reader.End();
                    log.Add("shm-write " + offset + " " + ReferenceFixtures.Hex(data));
                    Array.Copy(data, 0, memory, offset, data.Length);
                    break;
                }

                case 0xCC:
                    sharedHandles.Remove((uint)reader.U32());
                    reader.End();
                    log.Add("shm-close");
                    break;
                case 0x83:
                {
                    reader.U8();
                    string text = reader.Text();
                    reader.End();
                    log.Add("event " + pendingText + text);
                    pendingText = string.Empty;
                    break;
                }

                case 0x85:
                    pendingText += reader.Text();
                    reader.End();
                    break;
                case 0xB7:
                    log.Add("service-run " + reader.Text());
                    reader.End();
                    harness.Post(new Frame(0x40, 0, 0, new byte[0]));
                    break;
                case 0xBE:
                    reader.End();
                    log.Add("stop-self");
                    harness.Post(new Frame(0x41, 0, 0, new byte[0]));
                    break;
                case 0xB0:
                case 0xB1:
                case 0xB2:
                case 0xB3:
                case 0xB4:
                case 0xB5:
                {
                    byte[] answer = Registry.Handle(frame.Type, reader);
                    RegistryRequests++;
                    harness.Post(new Frame(0x30, 0, frame.Sequence, answer));
                    break;
                }

                case 0xB6:
                {
                    string path = reader.Text();
                    reader.End();
                    log.Add("file-info " + path);
                    string[] identity;
                    var answer = new PayloadWriter();
                    if (Files.TryGetValue(path, out identity))
                    {
                        answer.U32(0).Text(identity[0]).Text(identity[1]);
                    }
                    else
                    {
                        answer.U32(2).Text(string.Empty).Text(string.Empty);
                    }

                    harness.Post(new Frame(0x32, 0, frame.Sequence, answer.ToArray()));
                    break;
                }

                case 0x86:
                    reader.End();
                    harness.Post(new Frame(0x06, 0, frame.Sequence, new PayloadWriter().Text(HostDirectory).ToArray()));
                    break;
                case 0xC0:
                    reader.End();
                    harness.Post(new Frame(0x20, 0, frame.Sequence, new byte[0]));
                    foreach (KeyValuePair<int, string> process in Processes)
                    {
                        harness.Post(new Frame(0x21, 0, frame.Sequence, new PayloadWriter().U32(process.Key).Text(process.Value).ToArray()));
                    }

                    harness.Post(new Frame(0x22, 0, frame.Sequence, new PayloadWriter().U16(Processes.Count).ToArray()));
                    break;
                case 0xF0:
                    Legacy = reader.U8();
                    reader.End();
                    break;
                default:
                    throw new InvalidOperationException("Unexpected command frame " + frame);
            }
        }

        private void Enumerate(ushort sequence)
        {
            Enumerations++;
            tokens.Clear();
            harness.Post(new Frame(0x10, 0, sequence, new byte[0]));
            var devices = new List<FakeDevice>();
            devices.Add(Unrelated[0]);
            devices.AddRange(phases[current].Devices);
            devices.AddRange(Unrelated.GetRange(1, Unrelated.Count - 1));
            foreach (FakeDevice device in devices)
            {
                device.Token = nextToken++;
                tokens[device.Token] = device;
                harness.Post(new Frame(0x11, 0, sequence, new PayloadWriter()
                    .U32(device.Token).U16(device.Vendor).U16(device.Product).U16(device.Version)
                    .U16(device.UsagePage).U16(device.Usage).U16(device.InputLength).U16(device.OutputLength)
                    .Text(@"\\?\hid#" + device.Path).Text(device.Name).ToArray()));
            }

            harness.Post(new Frame(0x12, 0, sequence, new PayloadWriter().U16(devices.Count).ToArray()));
        }

        private FakeDevice Device(long token)
        {
            FakeDevice device;
            if (!tokens.TryGetValue((uint)token, out device))
            {
                throw new InvalidOperationException("The program used an unknown or stale token " + token);
            }

            return device;
        }

        internal static FakeDevice Native(string path)
        {
            return new FakeDevice(path, "Test G29", 0x046D, 0xC24F, 0x8900, 0x01, 0x04, 13, 17);
        }

        internal static FakeDevice Compatibility(string path)
        {
            return new FakeDevice(path, "Test G29 compat", 0x046D, 0xC294, 0x1350, 0x01, 0x04, 12, 7);
        }

        internal static FakeDevice Ps4(string path)
        {
            return new FakeDevice(path, "Test G29 PS4", 0x046D, 0xC260, 0x8900, 0x01, 0x05, 64, 32);
        }

        internal sealed class Phase
        {
            internal Phase(params FakeDevice[] devices)
            {
                Devices = devices;
            }

            internal FakeDevice[] Devices { get; private set; }
        }

        internal sealed class FakeDevice
        {
            internal FakeDevice(string path, string name, int vendor, int product, int version, int usagePage, int usage, int inputLength, int outputLength)
            {
                Path = path;
                Name = name;
                Vendor = vendor;
                Product = product;
                Version = version;
                UsagePage = usagePage;
                Usage = usage;
                InputLength = inputLength;
                OutputLength = outputLength;
            }

            internal string Path { get; private set; }

            internal string Name { get; private set; }

            internal int Vendor { get; private set; }

            internal int Product { get; private set; }

            internal int Version { get; private set; }

            internal int UsagePage { get; private set; }

            internal int Usage { get; private set; }

            internal int InputLength { get; private set; }

            internal int OutputLength { get; private set; }

            internal uint Token { get; set; }
        }
    }
}
