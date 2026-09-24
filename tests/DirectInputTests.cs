using System;
using System.Collections.Generic;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // Role DIRECTINPUT (src/brainfuck/directinput.bfa) against a frame-level fake of
    // the COM shell and its HID/shared-memory/timer mechanisms. Expected results
    // follow the legacy G29EffectDriver/ForceFeedbackSession.
    internal static class DirectInputTests
    {
        private const uint Ok = 0;
        private const uint InvalidParam = 0x80070057;
        private const uint Generic = 0x80004005;
        private const uint Unsupported = 0x80004001;
        private const uint NotInitialized = 0x80070015;
        private const uint DeviceFull = 0x80040201;

        internal static void Run()
        {
            NoSession();
            DeviceIdentity();
            SessionOpenFailures();
            SessionLifecycle();
            Effects();
            EffectErrors();
            ConditionEffectUsesSteering();
            DeviceLost();
            Polling();
            Console.WriteLine("  DirectInput role: worst force tick {0} VM steps", Fake.WorstTick);
        }

        private static void NoSession()
        {
            var di = new Fake();
            Assert.True(di.SetGain(5000).Result == NotInitialized, "SetGain needs a session");
            Assert.True(di.Command(1).Result == NotInitialized, "commands need a session");
            Assert.True(di.State().Result == NotInitialized, "state needs a session");
            Assert.True(di.Escape().Result == Unsupported, "Escape is unsupported");
            Answer versions = di.Versions(true, 16);
            Assert.True(versions.Result == Ok && versions.Output[4] == 0 && versions.Output[8] == 0 && versions.Output[12] == 0x00010000, "versions without a session");
            Assert.True(di.Versions(true, 15).Result == InvalidParam, "short DIDRIVERVERSIONS");
            Assert.True(di.Versions(false, 16).Result == InvalidParam, "no DIDRIVERVERSIONS");
            Assert.True(di.DeviceId(0).Result == Ok, "DeviceID end without a session");
            Assert.True(di.Commands.Count == 0, "no device traffic without a session");
        }

        private static void DeviceIdentity()
        {
            Assert.True(new Fake().DeviceId(1, present: false).Result == InvalidParam, "no DIHIDFFINITINFO");
            Assert.True(new Fake().DeviceId(1, size: 8).Result == InvalidParam, "short DIHIDFFINITINFO");
            AssertRefused(new Fake { Vendor = 0x046E }, "another vendor");
            AssertRefused(new Fake { Product = 0xC260 }, "the PS4-mode product");
            AssertRefused(new Fake { Product = 0xC294 }, "a compatibility product (not native)");
            AssertRefused(new Fake { Inspected = false }, "an interface that could not be inspected");
        }

        private static void AssertRefused(Fake di, string name)
        {
            Assert.True(di.DeviceId(1).Result == Generic, name + " is refused");
            Assert.True(di.Commands.Count == 0, name + ": nothing opened");
            Assert.True(di.SetGain(1).Result == NotInitialized, name + ": no session");
        }

        private static void SessionOpenFailures()
        {
            var di = new Fake { OpenError = 5 };
            Assert.True(di.DeviceId(1).Result == Generic, "open failure");
            Assert.True(di.Types() == "91", "nothing else after the failed open");

            di = new Fake { CapsStatus = 1 };
            Assert.True(di.DeviceId(1).Result == Generic, "no steering axis");
            Assert.True(di.Types() == "91 97 92", "the output handle is closed again");

            di = new Fake { ReadError = 5 };
            Assert.True(di.DeviceId(1).Result == Generic, "input cannot be read");
            Assert.True(di.Types() == "91 97 94 92", "the output handle is closed again");
            Assert.True(di.SetGain(1).Result == NotInitialized, "no session after a failure");
        }

        private static void SessionLifecycle()
        {
            var di = new Fake();
            Assert.True(di.DeviceId(1).Result == Ok, "session opens");
            Assert.True(di.AllTypes() == "91 97 94 96 C9 A0", "open, axis, read, poll, heartbeat, timer");
            Assert.True(di.TimerArmed && di.TimerInterval == 2, "2 ms force timer");
            Assert.True(di.ShmName == @"Global\G29Standalone.ForceHeartbeat", "heartbeat mapping name");
            Answer versions = di.Versions(true, 16);
            Assert.True(versions.Output[4] == 0x8900 && versions.Output[8] == 0x8900 && versions.Output[12] == 0x00010000, "versions report the wheel revision");
            Answer state = di.State();
            Assert.True(state.Result == Ok && state.Output[4] == 0x40 + 0x01 + 0x02 + 0x10 && state.Output[8] == 0, "empty, stopped, actuators on");

            // the first tick sends the zero force once and beats
            di.Commands.Clear();
            di.Tick(1000000);
            Assert.True(di.Types() == "93 CB", "first tick: stop report and heartbeat");
            Assert.True(Hex(di.LastWrite) == "13 00 00 00 00 00 00", "zero force is the stop report");
            Assert.True(di.LastBeatActive == 0 && di.LastBeatTick == 1000, "inactive beat with the system tick");
            di.Commands.Clear();
            di.Tick(1002000);
            Assert.True(di.Types() == "CB", "unchanged force is not resent");

            // the session ends: timer, stop report, inactive beat, handles
            di.Commands.Clear();
            Assert.True(di.DeviceId(0).Result == Ok, "session ends");
            Assert.True(di.Types() == "A1 93 CB CC 95 92", "timer, stop, beat, heartbeat, reader, device");
            Assert.True(Hex(di.LastWrite) == "13 00 00 00 00 00 00", "stop report at the end");
            Assert.True(di.SetGain(1).Result == NotInitialized, "no session after the end");

            // process exit ends the session too
            di = new Fake();
            di.DeviceId(1);
            di.Commands.Clear();
            di.Shutdown();
            Assert.True(di.Types() == "A1 93 CB CC 95 92 80", "shutdown stops the wheel, then exits");
        }

        private static void Effects()
        {
            var di = new Fake();
            di.DeviceId(1);
            Answer created = di.Download(1, 0, "3C5", "4:5000", 1);
            Assert.True(created.Result == Ok && created.Output[0] == 1, "a constant force effect is created as handle 1");
            Answer started = di.Download(1, 1, "20000000", "4:5000", 1);
            Assert.True(started.Result == Ok && !started.Output.ContainsKey(0), "an existing effect is updated (no new handle)");
            di.Commands.Clear();
            di.Tick(2000000);
            // force 5000 -> percent +50 -> 11 08 C0 80 ... (DirectInput positive
            // force pushes left; measured: a positive protocol percent turns the G29 left)
            Assert.True(di.Types() == "93 CB" && Hex(di.LastWrite) == "11 08 C0 80 00 00 00", "positive DirectInput force is a positive protocol percent");
            Assert.True(di.LastBeatActive == 1, "active beat while force is applied");
            Assert.True(di.Status(1).Output[0] == 1, "effect playing");
            Answer state = di.State();
            Assert.True(state.Output[4] == 0x40 + 0x10 && state.Output[8] == 3, "one playing effect, load 3 %");

            Assert.True(di.SetGain(5000).Result == Ok, "device gain");
            di.Commands.Clear();
            di.Tick(2002000);
            Assert.True(Hex(di.LastWrite) == "11 08 A0 80 00 00 00", "half gain halves the force");
            Assert.True(di.SetGain(20000).Result == Ok, "gain above full scale is clamped");
            di.Commands.Clear();
            di.Tick(2004000);
            Assert.True(Hex(di.LastWrite) == "11 08 C0 80 00 00 00", "clamped gain is full scale");

            Assert.True(di.Command(0x04).Result == Ok, "pause");
            Assert.True((di.State().Output[4] & 0x04) != 0, "paused");
            di.Commands.Clear();
            di.Tick(2006000);
            Assert.True(Hex(di.LastWrite) == "13 00 00 00 00 00 00", "paused: no force");
            Assert.True(di.Command(0x08).Result == Ok, "continue");
            Assert.True(di.Command(0x20).Result == Ok, "actuators off");
            Assert.True((di.State().Output[4] & 0x20) != 0, "actuators off state");
            Assert.True(di.Command(0x10).Result == Ok, "actuators on");
            Assert.True(di.Command(0x40).Result == InvalidParam, "unknown command");
            Assert.True(di.Command(0x03).Result == InvalidParam, "two commands at once");

            Assert.True(di.Stop(1).Result == Ok, "stop effect");
            Assert.True(di.Status(1).Output[0] == 0, "stopped");
            Assert.True(di.Start(1, 0x01, 1).Result == Ok, "start solo");
            Assert.True(di.Status(1).Output[0] == 1, "playing again");
            Assert.True(di.Start(1, 0, 0xFFFFFFFF).Result == Ok, "start infinitely");
            Assert.True(di.Command(0x02).Result == Ok, "stop all");
            Assert.True(di.Status(1).Output[0] == 0, "stop all stops");
            Assert.True(di.Destroy(1).Result == Ok, "destroy");
            Assert.True(di.Status(1).Result == InvalidParam, "destroyed handle is unknown");
            Assert.True((di.State().Output[4] & 0x01) != 0, "empty again");

            // Reset clears the table; handles keep counting
            Assert.True(di.Download(1, 0, "105", "4:100", 1).Output[0] == 2, "next handle");
            Assert.True(di.Command(0x01).Result == Ok, "reset");
            Assert.True(di.Status(2).Result == InvalidParam, "reset removed the effect");
            Assert.True(di.Download(1, 0, "105", "4:100", 1).Output[0] == 3, "handles are not reused after a reset");

            // a new session starts a new table
            di.DeviceId(1);
            Assert.True(di.Download(1, 0, "105", "4:100", 1).Output[0] == 1, "a new session starts at handle 1");
        }

        private static void EffectErrors()
        {
            var di = new Fake();
            di.DeviceId(1);
            Assert.True(di.Download(12, 0, "105", "4:100", 1).Result == InvalidParam, "effect id above friction");
            Assert.True(di.Download(0, 0, "105", "4:100", 1).Result == InvalidParam, "effect id 0");
            Assert.True(di.Download(1, 0, "105", "4:100", 1, handlePresent: false).Result == InvalidParam, "no handle pointer");
            Assert.True(di.Download(1, 0, "105", "4:100", 1, size: 71).Result == InvalidParam, "DIEFFECT too small");
            Assert.True(di.Download(1, 0, "105", "0:-", 1).Result == InvalidParam, "missing type-specific data");
            Assert.True(di.Download(2, 0, "105", "4:100", 1).Result == InvalidParam, "short ramp data");
            Answer first = di.Download(1, 0, "105", "4:100", 1);
            Assert.True(first.Result == Ok && first.Output[0] == 1, "failed downloads create nothing");
            Assert.True(di.Download(4, 1, "105", "16:100,0,0,1000", 1).Result == InvalidParam, "an existing effect keeps its kind");
            Assert.True(di.Download(1, 99, "105", "4:100", 1).Output[0] == 2, "an unknown handle creates a new effect");
            for (int index = 3; index <= 32; index++)
            {
                Assert.True(di.Download(1, 0, "105", "4:100", 1).Output[0] == index, "effect " + index);
            }

            Assert.True(di.Download(1, 0, "105", "4:100", 1).Result == DeviceFull, "33rd effect");
            Assert.True(di.Destroy(77).Result == InvalidParam, "destroy unknown");
            Assert.True(di.Stop(77).Result == InvalidParam, "stop unknown");
            Assert.True(di.Start(77, 0, 1).Result == InvalidParam, "start unknown");
            Assert.True(di.Status(1, false).Result == InvalidParam, "no status pointer");
            Assert.True(di.State(false).Result == InvalidParam, "no state pointer");
            Assert.True(di.State(true, 11).Result == InvalidParam, "short state");
        }

        private static void ConditionEffectUsesSteering()
        {
            // full right lock (raw 65535) and a spring
            var di = new Fake { PollValue = 65535 };
            di.DeviceId(1);
            Assert.True(di.Download(8, 0, "100", "24:0,10000,10000,10000,10000,0", 0x20000000).Result == Ok, "spring");
            di.Commands.Clear();
            di.Tick(3000000);
            // the spring pulls the wheel back from full right lock: DirectInput +10000
            // (toward the left), percent +100
            Assert.True(Hex(di.LastWrite) == Hex(ForceReport(10000)), "spring force from the polled steering position");

            // a steering report moves the wheel to the centre: no force
            di.Input(32767, 3001000);
            di.Input(32768, 3001500);
            di.Commands.Clear();
            di.Tick(3002000);
            Assert.True(Hex(di.LastWrite) == "13 00 00 00 00 00 00", "centred wheel: spring at rest");
        }

        private static void DeviceLost()
        {
            var di = new Fake();
            di.DeviceId(1);
            di.Download(1, 0, "105", "4:5000", 0x20000000);
            di.WriteError = 1167;
            di.Commands.Clear();
            di.Tick(4000000);
            Assert.True(di.Types() == "93 CB A1", "failed write ends the force loop");
            Assert.True(!di.TimerArmed, "timer cancelled");
            Assert.True((di.State().Output[4] & 0x80000000) != 0, "DIGFFS_DEVICELOST");
            di.Commands.Clear();
            di.Tick(4002000);
            Assert.True(di.Types() == string.Empty, "no more writes or beats after the loss");
            di.Commands.Clear();
            di.DeviceId(0);
            Assert.True(di.Types() == "A1 93 CB CC 95 92", "the session end still tries the stop report");
        }

        private static void Polling()
        {
            var di = new Fake { PollValue = null };
            di.DeviceId(1);
            di.Commands.Clear();
            di.Tick(5000000);
            Assert.True(di.AllTypes() == "96 93 CB", "no steering sample yet: poll");
            di.Commands.Clear();
            di.Tick(5050000);
            Assert.True(di.AllTypes() == "CB", "at most one poll per 100 ms");
            di.Commands.Clear();
            di.Tick(5100000);
            Assert.True(di.AllTypes() == "96 CB", "poll again after 100 ms");
            di.Input(40000, 5101000);
            di.Commands.Clear();
            di.Tick(5300000);
            Assert.True(di.AllTypes() == "CB", "a recent sample needs no poll");
            di.Commands.Clear();
            di.Tick(5351001);
            Assert.True(di.AllTypes() == "96 CB", "no sample for more than 250 ms: poll");
        }

        // legacy: percent = -round(force / 100) half away from zero
        private static byte[] ForceReport(int force)
        {
            int percent = (int)Math.Round(force / 100.0, MidpointRounding.AwayFromZero);
            if (percent == 0)
            {
                return new byte[] { 0x13, 0, 0, 0, 0, 0, 0 };
            }

            int magnitude = Math.Abs(percent);
            int offset = ((127 * magnitude) + 50) / 100;
            return new byte[] { 0x11, 0x08, (byte)(percent < 0 ? 128 - offset : 128 + offset), 0x80, 0, 0, 0 };
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace('-', ' ');
        }

        private sealed class Answer
        {
            internal uint Result;
            internal Dictionary<int, uint> Output = new Dictionary<int, uint>();
        }

        private sealed class Fake
        {
            private readonly BfHarness harness;
            private ushort sequence = 100;
            private long clock;

            internal Fake()
            {
                Vendor = 0x046D;
                Product = 0xC24F;
                Inspected = true;
                PollValue = 32767;
                Commands = new List<Frame>();
                Writes = new List<byte[]>();
                harness = new BfHarness(BfMainTests.Program, BfVm.DefaultStepBudget);
                harness.OnCommand = OnCommand;
                harness.Post(BfMainTests.Boot(3));
                harness.Run();
            }

            internal int Vendor { get; set; }

            internal int Product { get; set; }

            internal bool Inspected { get; set; }

            internal int OpenError { get; set; }

            internal int CapsStatus { get; set; }

            internal int ReadError { get; set; }

            internal uint? PollValue { get; set; }

            internal int WriteError { get; set; }

            internal List<Frame> Commands { get; private set; }

            internal List<byte[]> Writes { get; private set; }

            internal byte[] LastWrite
            {
                get { return Writes[Writes.Count - 1]; }
            }

            internal bool TimerArmed { get; private set; }

            internal long TimerInterval { get; private set; }

            internal string ShmName { get; private set; }

            internal long LastBeatTick { get; private set; }

            internal long LastBeatActive { get; private set; }

            // Command types in order, without the steering polls (see Polling).
            internal string Types()
            {
                return AllTypes().Replace("96 ", string.Empty).Replace(" 96", string.Empty).Replace("96", string.Empty);
            }

            internal string AllTypes()
            {
                var parts = new List<string>();
                foreach (Frame frame in Commands)
                {
                    parts.Add(frame.Type.ToString("X2"));
                }

                return string.Join(" ", parts.ToArray());
            }

            internal Answer DeviceId(uint begin, bool present = true, int size = 16)
            {
                return Call(0x50, new PayloadWriter().U32(0x0800).U32(0).U32(begin).U32(0)
                    .U8(present ? 1 : 0).U32(size).U32(16).U8(Inspected ? 1 : 0)
                    .U32(9).U16(Vendor).U16(Product).U16(0x8900).U16(1).U16(4).U16(12).U16(7));
            }

            internal Answer Versions(bool present, int size)
            {
                return Call(0x51, new PayloadWriter().U8(present ? 1 : 0).U32(size));
            }

            internal Answer Escape()
            {
                return Call(0x52, new PayloadWriter().U32(0).U32(0));
            }

            internal Answer SetGain(uint gain)
            {
                return Call(0x53, new PayloadWriter().U32(0).U32(gain));
            }

            internal Answer Command(uint command)
            {
                return Call(0x54, new PayloadWriter().U32(0).U32(command));
            }

            internal Answer State(bool present = true, int size = 12)
            {
                return Call(0x55, new PayloadWriter().U32(0).U8(present ? 1 : 0).U32(size));
            }

            internal Answer Download(uint effectId, uint handle, string changed, string typeSpecific, uint extraChanged, bool handlePresent = true, int size = 80)
            {
                uint flags = Convert.ToUInt32(changed, 16) | (extraChanged & 0x20000000);
                var fields = new Dictionary<string, string>
                {
                    { "changed", flags.ToString("X") },
                    { "size", size.ToString() },
                    { "flags", "10" },
                    { "dur", "4294967295" },
                    { "gain", "10000" },
                    { "axes", "1" },
                    { "dir", "1" },
                    { "env", "-" },
                    { "ts", typeSpecific },
                    { "delay", "0" }
                };
                var payload = new PayloadWriter().U32(0).U32(effectId).U8(handlePresent ? 1 : 0).U32(handle);
                payload.Bytes(ReaderParityTests.Serialize(fields));
                return Call(0x56, payload);
            }

            internal Answer Destroy(uint handle)
            {
                return Call(0x57, new PayloadWriter().U32(0).U32(handle));
            }

            internal Answer Start(uint handle, uint mode, uint count)
            {
                return Call(0x58, new PayloadWriter().U32(0).U32(handle).U32(mode).U32(count));
            }

            internal Answer Stop(uint handle)
            {
                return Call(0x59, new PayloadWriter().U32(0).U32(handle));
            }

            internal Answer Status(uint handle, bool present = true)
            {
                return Call(0x5A, new PayloadWriter().U32(0).U32(handle).U8(present ? 1 : 0));
            }

            internal static long WorstTick { get; private set; }

            internal void Tick(long now)
            {
                clock = now;
                harness.Post(new Frame(0x02, 0, 0, new PayloadWriter().U8(1).U64(now).U64(now / 1000).ToArray()));
                long before = harness.Machine.TotalSteps;
                harness.Run();
                WorstTick = Math.Max(WorstTick, harness.Machine.TotalSteps - before);
            }

            internal void Input(uint value, long now)
            {
                clock = now;
                harness.Post(new Frame(0x15, 0, 0, new PayloadWriter().U32(9).U32(value).U64(now).ToArray()));
                harness.Run();
            }

            internal void Shutdown()
            {
                harness.Post(new Frame(0x03, 0, 0, new byte[0]));
                harness.Run();
            }

            private Answer Call(byte type, PayloadWriter arguments)
            {
                sequence++;
                var payload = new PayloadWriter().U64(clock).Bytes(arguments.ToArray());
                harness.Post(new Frame(type, 0, sequence, payload.ToArray()));
                IList<Frame> frames = harness.Run();
                Answer answer = null;
                foreach (Frame frame in frames)
                {
                    if (frame.Type == 0xD0)
                    {
                        Assert.True(answer == null && frame.Sequence == sequence, "one CMD_DI_RETURN with the call's sequence");
                        var reader = new PayloadReader(frame.Payload);
                        answer = new Answer { Result = (uint)reader.U32() };
                        int count = reader.U8();
                        for (int index = 0; index < count; index++)
                        {
                            int offset = reader.U16();
                            answer.Output[offset] = (uint)reader.U32();
                        }

                        reader.End();
                    }
                }

                Assert.True(answer != null, "the call was answered");
                return answer;
            }

            private void OnCommand(Frame frame)
            {
                var reader = new PayloadReader(frame.Payload);
                switch (frame.Type)
                {
                    case 0xD0:
                    case 0x84:
                        if (frame.Type == 0x84)
                        {
                            throw new InvalidOperationException("ABI error " + frame.Payload[0] + " for event " + frame.Payload[1].ToString("X2"));
                        }

                        return;
                    case 0x91:
                        Post(0x13, new PayloadWriter().U32(reader.U32()).U32(OpenError));
                        break;
                    case 0x97:
                    {
                        long token = reader.U32();
                        Assert.True(reader.U16() == 1 && reader.U16() == 0x30, "steering axis usage");
                        Post(0x16, new PayloadWriter().U32(token).U32(CapsStatus).U32(0).U32(65535).U16(16));
                        break;
                    }

                    case 0x94:
                    {
                        long token = reader.U32();
                        Assert.True(reader.U16() == 1 && reader.U16() == 0x30, "reads the steering axis");
                        Post(0x13, new PayloadWriter().U32(token).U32(ReadError));
                        break;
                    }

                    case 0x96:
                    {
                        long token = reader.U32();
                        if (PollValue.HasValue)
                        {
                            Post(0x15, new PayloadWriter().U32(token).U32(PollValue.Value).U64(clock));
                        }

                        Post(0x18, new PayloadWriter().U32(token).U32(PollValue.HasValue ? 0 : 31));
                        break;
                    }

                    case 0xC9:
                        ShmName = reader.Text();
                        Assert.True(reader.U32() == 16, "heartbeat size");
                        Post(0x33, new PayloadWriter().U32(0).U32(7));
                        break;
                    case 0xCB:
                    {
                        Assert.True(reader.U32() == 7 && reader.U32() == 0, "heartbeat handle and offset");
                        byte[] bytes = reader.Bytes(reader.U16());
                        Assert.True(bytes.Length == 12, "beat and active flag");
                        LastBeatTick = BitConverter.ToInt64(bytes, 0);
                        LastBeatActive = BitConverter.ToInt32(bytes, 8);
                        break;
                    }

                    case 0x93:
                    {
                        Assert.True(reader.U32() == 9, "writes go to the session's wheel");
                        byte[] bytes = reader.Bytes(reader.U16());
                        Writes.Add(bytes);
                        Post(0x14, new PayloadWriter().U32(9).U32(WriteError));
                        break;
                    }

                    case 0xA0:
                        Assert.True(reader.U8() == 1 && reader.U8() == 1, "repeating timer 1");
                        TimerInterval = reader.U32();
                        TimerArmed = true;
                        break;
                    case 0xA1:
                        TimerArmed = false;
                        break;
                }

                Commands.Add(frame);
            }

            private void Post(byte type, PayloadWriter payload)
            {
                harness.Post(new Frame(type, 0, 0, payload.ToArray()));
            }
        }
    }
}
