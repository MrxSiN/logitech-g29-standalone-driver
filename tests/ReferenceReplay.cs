using System;
using System.Collections.Generic;

namespace G29.Tests
{
    // Replays the frozen legacy vectors against the program. Each replay takes the
    // implementation under test through a small adapter.
    internal static class ReferenceReplay
    {
        internal static void Run()
        {
            Protocol(new ProgramProtocolUnderTest());
        }

        internal interface IProtocolUnderTest
        {
            int Identify(int productId, int revision);

            string Native();

            string Stop();

            string Range(int degrees);

            string AutoCenter(int percent);

            string Leds(int mask);

            string Force(int percent);
        }

        // Operation results use the fixture vocabulary (h=N, full, range, ok, unknown,
        // 0/1, numbers).
        internal interface IEngineUnderTest
        {
            string Create(EffectParameters parameters);

            string Update(int handle, EffectParameters parameters);

            string Start(int handle, uint iterations, bool solo, long now);

            string Stop(int handle);

            string Destroy(int handle);

            string StopAll();

            string Reset();

            string Pause(long now);

            string Continue(long now);

            string Actuators(bool enabled);

            string Gain(uint gain);

            int Force(long now, int position, int velocity, int acceleration);

            string Playing(int handle, long now);

            string Any(long now);

            string Count();

            string Paused();

            string ActuatorsOn();
        }

        internal static void Protocol(IProtocolUnderTest protocol)
        {
            int checkedLines = 0;
            foreach (string line in ReferenceFixtures.Lines("protocol.txt"))
            {
                string[] parts = line.Split(' ');
                string actual;
                string expected;
                switch (parts[0])
                {
                    case "identify":
                        expected = parts[3];
                        actual = protocol.Identify(ReferenceFixtures.HexInt(parts[1]), ReferenceFixtures.HexInt(parts[2])).ToString();
                        break;
                    case "native":
                        expected = Tail(parts, 1);
                        actual = protocol.Native();
                        break;
                    case "stop":
                        expected = Tail(parts, 1);
                        actual = protocol.Stop();
                        break;
                    case "range":
                        expected = Tail(parts, 2);
                        actual = protocol.Range(int.Parse(parts[1]));
                        break;
                    case "autocenter":
                        expected = Tail(parts, 2);
                        actual = protocol.AutoCenter(int.Parse(parts[1]));
                        break;
                    case "leds":
                        expected = Tail(parts, 2);
                        actual = protocol.Leds(int.Parse(parts[1]));
                        break;
                    case "force":
                        expected = Tail(parts, 2);
                        actual = protocol.Force(int.Parse(parts[1]));
                        break;
                    default:
                        throw new InvalidOperationException("Unknown protocol fixture line: " + line);
                }

                if (expected != actual)
                {
                    throw new InvalidOperationException(string.Format("Protocol vector mismatch: '{0}' produced '{1}'.", line, actual));
                }

                checkedLines++;
            }

            Assert.True(checkedLines > 4000, "protocol fixture covers every input");
        }

        // tolerance: allowed absolute difference for periodic (sine) force samples;
        // every other result must match exactly.
        internal static void Engine(Func<IEngineUnderTest> factory, int sineTolerance)
        {
            IEngineUnderTest engine = null;
            string scenario = null;
            var handles = new Dictionary<int, int>();
            int checkedLines = 0;
            foreach (string line in ReferenceFixtures.Lines("ffb-engine.txt"))
            {
                int arrow = line.IndexOf(" => ", StringComparison.Ordinal);
                string[] parts = (arrow < 0 ? line : line.Substring(0, arrow)).Split(' ');
                string expected = arrow < 0 ? null : line.Substring(arrow + 4);
                if (parts[0] == "new")
                {
                    engine = factory();
                    scenario = parts[1];
                    handles.Clear();
                    continue;
                }

                string actual;
                switch (parts[0])
                {
                    case "create":
                        actual = engine.Create(Parameters(parts, 1));
                        break;
                    case "update":
                        actual = engine.Update(Handle(parts[1]), Parameters(parts, 2));
                        break;
                    case "start":
                        actual = engine.Start(Handle(parts[1]), uint.Parse(parts[2]), parts[3] == "1", ReferenceFixtures.Long(parts[4]));
                        break;
                    case "stop":
                        actual = engine.Stop(Handle(parts[1]));
                        break;
                    case "destroy":
                        actual = engine.Destroy(Handle(parts[1]));
                        break;
                    case "stopall":
                        actual = engine.StopAll();
                        break;
                    case "reset":
                        actual = engine.Reset();
                        break;
                    case "pause":
                        actual = engine.Pause(ReferenceFixtures.Long(parts[1]));
                        break;
                    case "continue":
                        actual = engine.Continue(ReferenceFixtures.Long(parts[1]));
                        break;
                    case "actuators":
                        actual = engine.Actuators(parts[1] == "1");
                        break;
                    case "gain":
                        actual = engine.Gain(uint.Parse(parts[1]));
                        break;
                    case "force":
                        int force = engine.Force(ReferenceFixtures.Long(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]));
                        int wanted = int.Parse(expected);
                        int tolerance = scenario.StartsWith("periodic", StringComparison.Ordinal) || scenario.StartsWith("sine", StringComparison.Ordinal) || scenario.StartsWith("mix", StringComparison.Ordinal) || scenario.StartsWith("envelope-periodic", StringComparison.Ordinal) ? sineTolerance : 0;
                        actual = Math.Abs(force - wanted) <= tolerance ? expected : force.ToString();
                        break;
                    case "playing":
                        actual = engine.Playing(Handle(parts[1]), ReferenceFixtures.Long(parts[2]));
                        break;
                    case "any":
                        actual = engine.Any(ReferenceFixtures.Long(parts[1]));
                        break;
                    case "count":
                        actual = engine.Count();
                        break;
                    case "paused":
                        actual = engine.Paused();
                        break;
                    case "actuatorson":
                        actual = engine.ActuatorsOn();
                        break;
                    default:
                        throw new InvalidOperationException("Unknown engine fixture line: " + line);
                }

                if (actual != expected)
                {
                    throw new InvalidOperationException(string.Format("Engine vector mismatch in {0}: '{1}' produced '{2}'.", scenario, line, actual));
                }

                checkedLines++;
            }

            Assert.True(checkedLines > 10000, "engine fixture replayed");
        }

        // Fixture handles are the legacy handles; implementations must hand out the
        // same numbers, so they are used verbatim.
        private static int Handle(string text)
        {
            return int.Parse(text);
        }

        internal static EffectParameters Parameters(string[] parts, int start)
        {
            var parameters = new EffectParameters((EffectKind)int.Parse(parts[start]));
            parameters.Duration = uint.Parse(parts[start + 1]);
            parameters.Gain = uint.Parse(parts[start + 2]);
            parameters.StartDelay = uint.Parse(parts[start + 3]);
            parameters.DirectionSign = int.Parse(parts[start + 4]);
            if (parts[start + 5] != "-")
            {
                string[] envelope = parts[start + 5].Split(',');
                parameters.Envelope = new EffectEnvelope { AttackLevel = uint.Parse(envelope[0]), AttackTime = uint.Parse(envelope[1]), FadeLevel = uint.Parse(envelope[2]), FadeTime = uint.Parse(envelope[3]) };
            }

            parameters.Magnitude = int.Parse(parts[start + 6]);
            parameters.RampStart = int.Parse(parts[start + 7]);
            parameters.RampEnd = int.Parse(parts[start + 8]);
            parameters.Offset = int.Parse(parts[start + 9]);
            parameters.Phase = uint.Parse(parts[start + 10]);
            parameters.Period = uint.Parse(parts[start + 11]);
            string[] condition = parts[start + 12].Split(',');
            parameters.Condition = new EffectCondition
            {
                Offset = int.Parse(condition[0]),
                PositiveCoefficient = int.Parse(condition[1]),
                NegativeCoefficient = int.Parse(condition[2]),
                PositiveSaturation = uint.Parse(condition[3]),
                NegativeSaturation = uint.Parse(condition[4]),
                DeadBand = int.Parse(condition[5])
            };
            return parameters;
        }

        private static string Tail(string[] parts, int start)
        {
            return string.Join(" ", parts, start, parts.Length - start);
        }

        private sealed class ProgramProtocolUnderTest : IProtocolUnderTest
        {
            private readonly ProgramProtocol protocol = new ProgramProtocol();

            public int Identify(int productId, int revision)
            {
                return (protocol.IsNative((ushort)productId) ? 1 : 0) |
                    (protocol.IsG29Hardware((ushort)productId, (ushort)revision) ? 2 : 0) |
                    (protocol.IsPs4Mode((ushort)productId, (ushort)revision) ? 4 : 0);
            }

            public string Native()
            {
                return ReferenceFixtures.Hex(protocol.SwitchToNative());
            }

            public string Stop()
            {
                return ReferenceFixtures.Hex(protocol.StopForce());
            }

            public string Range(int degrees)
            {
                return ReferenceFixtures.Hex(protocol.SetRange(degrees));
            }

            public string AutoCenter(int percent)
            {
                return ReferenceFixtures.Hex(protocol.SetAutoCenter(percent));
            }

            public string Leds(int mask)
            {
                return ReferenceFixtures.Hex(protocol.SetLeds(mask));
            }

            public string Force(int percent)
            {
                return ReferenceFixtures.Hex(protocol.SetConstantForce(percent));
            }
        }
    }
}
