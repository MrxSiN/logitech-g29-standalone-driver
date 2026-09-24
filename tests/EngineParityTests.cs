using System;
using System.Collections.Generic;
using System.Diagnostics;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The Brainfuck effect engine (role TEST 'G' entry points) against the frozen
    // legacy EffectEngine transcripts (tests/reference/ffb-engine.txt).
    internal static class EngineParityTests
    {
        internal static void Run()
        {
            var stopwatch = Stopwatch.StartNew();
            var engine = new ProgramEngine();
            ReferenceReplay.Engine(delegate { engine.Boot(); return engine; }, 1);
            Console.WriteLine("  engine transcripts: {0} ms, {1} force samples, worst force step {2} VM steps", stopwatch.ElapsedMilliseconds, engine.ForceSamples, engine.WorstForceSteps);
        }

        // One Brainfuck program per scenario, driven through EV_TEST 'G'.
        internal sealed class ProgramEngine : ReferenceReplay.IEngineUnderTest
        {
            private BfHarness harness;
            private ushort sequence;

            internal int ForceSamples { get; private set; }

            internal long WorstForceSteps { get; private set; }

            internal void Boot()
            {
                harness = new BfHarness(BfMainTests.Program, BfVm.DefaultStepBudget);
                harness.Post(BfMainTests.Boot(4));
                harness.Run();
            }

            private byte[] Call(char op, PayloadWriter arguments)
            {
                var payload = new PayloadWriter().U8('G').U8(op);
                if (arguments != null)
                {
                    payload.Bytes(arguments.ToArray());
                }

                sequence++;
                harness.Post(new Frame(0x05, 0, sequence, payload.ToArray()));
                IList<Frame> frames = harness.Run();
                if (frames.Count != 1 || frames[0].Type != 0x8F || frames[0].Payload.Length != 6)
                {
                    throw new InvalidOperationException("Unexpected engine answer for " + op);
                }

                return frames[0].Payload;
            }

            private static string Status(byte[] answer)
            {
                switch (answer[0])
                {
                    case 0:
                        return "ok";
                    case 1:
                        return "unknown";
                    case 2:
                        return "full";
                    default:
                        return "range";
                }
            }

            private static long Value(byte[] answer)
            {
                long value = answer[2] + ((long)answer[3] << 8) + ((long)answer[4] << 16) + ((long)answer[5] << 24);
                return answer[1] != 0 ? -value : value;
            }

            internal static PayloadWriter Signed(PayloadWriter writer, long value)
            {
                return writer.U8(value < 0 ? 1 : 0).U32(Math.Abs(value));
            }

            internal static PayloadWriter Params(EffectParameters p)
            {
                var writer = new PayloadWriter().U8((int)p.Kind).U32(p.Duration).U32(p.Gain).U32(p.StartDelay).U8(p.DirectionSign < 0 ? 1 : 0);
                EffectEnvelope envelope = p.Envelope;
                writer.U8(envelope == null ? 0 : 1);
                writer.U32(envelope == null ? 0 : envelope.AttackLevel).U32(envelope == null ? 0 : envelope.AttackTime);
                writer.U32(envelope == null ? 0 : envelope.FadeLevel).U32(envelope == null ? 0 : envelope.FadeTime);
                Signed(writer, p.Magnitude);
                Signed(writer, p.RampStart);
                Signed(writer, p.RampEnd);
                Signed(writer, p.Offset);
                writer.U32(p.Phase).U32(p.Period);
                Signed(writer, p.Condition.Offset);
                Signed(writer, p.Condition.PositiveCoefficient);
                Signed(writer, p.Condition.NegativeCoefficient);
                writer.U32(p.Condition.PositiveSaturation).U32(p.Condition.NegativeSaturation);
                Signed(writer, p.Condition.DeadBand);
                return writer;
            }

            public string Create(EffectParameters parameters)
            {
                byte[] answer = Call('c', Params(parameters));
                return answer[0] == 0 ? "h=" + Value(answer) : Status(answer);
            }

            public string Update(int handle, EffectParameters parameters)
            {
                var writer = new PayloadWriter().U32(handle);
                writer.Bytes(Params(parameters).ToArray());
                return Status(Call('u', writer));
            }

            public string Start(int handle, uint iterations, bool solo, long now)
            {
                bool infinite = iterations == EffectParameters.Infinite;
                return Status(Call('s', new PayloadWriter().U32(handle).U8(infinite ? 1 : 0).U32(infinite ? 0 : iterations).U8(solo ? 1 : 0).U64(now)));
            }

            public string Stop(int handle)
            {
                return Status(Call('p', new PayloadWriter().U32(handle)));
            }

            public string Destroy(int handle)
            {
                return Status(Call('d', new PayloadWriter().U32(handle)));
            }

            public string StopAll()
            {
                return Status(Call('A', null));
            }

            public string Reset()
            {
                return Status(Call('R', null));
            }

            public string Pause(long now)
            {
                return Status(Call('P', new PayloadWriter().U64(now)));
            }

            public string Continue(long now)
            {
                return Status(Call('C', new PayloadWriter().U64(now)));
            }

            public string Actuators(bool enabled)
            {
                return Status(Call('a', new PayloadWriter().U8(enabled ? 1 : 0)));
            }

            public string Gain(uint gain)
            {
                return Status(Call('g', new PayloadWriter().U32(gain)));
            }

            public int Force(long now, int position, int velocity, int acceleration)
            {
                var writer = new PayloadWriter().U64(now);
                Signed(writer, position);
                Signed(writer, velocity);
                Signed(writer, acceleration);
                long before = harness.Machine.TotalSteps;
                byte[] answer = Call('f', writer);
                long steps = harness.Machine.TotalSteps - before;
                ForceSamples++;
                if (steps > WorstForceSteps)
                {
                    WorstForceSteps = steps;
                }

                return (int)Value(answer);
            }

            public string Playing(int handle, long now)
            {
                byte[] answer = Call('y', new PayloadWriter().U32(handle).U64(now));
                return answer[0] == 1 ? "unknown" : Value(answer).ToString();
            }

            public string Any(long now)
            {
                return Value(Call('n', new PayloadWriter().U64(now))).ToString();
            }

            public string Count()
            {
                return Value(Call('k', null)).ToString();
            }

            public string Paused()
            {
                return Value(Call('q', null)).ToString();
            }

            public string ActuatorsOn()
            {
                return Value(Call('o', null)).ToString();
            }
        }
    }
}
