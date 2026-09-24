using System;
using System.Collections.Generic;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // Role CLI of the Brainfuck program against the frozen legacy transcripts:
    // tests/reference/cli.txt (g29ctl) and controller.txt (WheelController, driven
    // through the equivalent commands).
    internal static class CliParityTests
    {
        internal static void Run()
        {
            CliTranscripts();
            ControllerTranscripts();
            CancelAndTiming();
        }

        private static void CliTranscripts()
        {
            IList<string> lines = ReferenceFixtures.Lines("cli.txt");
            int cases = 0;
            for (int index = 0; index < lines.Count; index++)
            {
                string header = lines[index];
                if (!header.StartsWith("case ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unexpected cli.txt line " + header);
                }

                int split = header.LastIndexOf(" ; wheel ", StringComparison.Ordinal);
                string joined = header.Substring(5, split - 5);
                string[] arguments = joined.Length == 0 ? new string[0] : joined.Split('|');
                string wheel = header.Substring(split + 9);
                var expectedEvents = new List<string>();
                var expectedOut = new List<string>();
                var expectedErr = new List<string>();
                long expectedExit;
                while (true)
                {
                    string line = lines[++index];
                    if (line.StartsWith("exit ", StringComparison.Ordinal))
                    {
                        expectedExit = long.Parse(line.Substring(5));
                        break;
                    }

                    if (line.StartsWith("out", StringComparison.Ordinal))
                    {
                        expectedOut.Add(line.Length > 4 ? line.Substring(4) : string.Empty);
                    }
                    else if (line.StartsWith("err", StringComparison.Ordinal))
                    {
                        expectedErr.Add(line.Length > 4 ? line.Substring(4) : string.Empty);
                    }
                    else if (!line.StartsWith("enumerate ", StringComparison.Ordinal))
                    {
                        expectedEvents.Add(line);
                    }
                }

                FakeBridge bridge = Wheels(wheel);
                bridge.RunCli(arguments);
                string name = "cli case '" + joined + "' wheel " + wheel;
                Assert.True(bridge.ExitCode.HasValue, name + ": exits");
                Compare(expectedEvents, HidEvents(bridge), name + " HID transcript");
                Compare(expectedOut, Lines(bridge.Stdout), name + " stdout");
                Compare(expectedErr, Lines(bridge.Stderr), name + " stderr");
                Assert.True(bridge.ExitCode.Value == expectedExit, name + ": exit code " + expectedExit + ", got " + bridge.ExitCode.Value);
                cases++;
            }

            Assert.True(cases > 250, "every recorded g29ctl case replayed");
        }

        // controller.txt scenario -> the g29ctl command that performs it.
        private static readonly Dictionary<string, string[]> ControllerCommands = new Dictionary<string, string[]>
        {
            { "init-native-540-0", new[] { "init", "--range", "540", "--autocenter", "0" } },
            { "init-native-900-67", new[] { "init", "--range", "900", "--autocenter", "67" } },
            { "init-native-40-100", new[] { "init", "--range", "40", "--autocenter", "100" } },
            { "init-compatibility-switches-to-native", new[] { "init" } },
            { "init-compatibility-native-appears-beside-old", new[] { "init", "--range", "720", "--autocenter", "25" } },
            { "init-compatibility-never-returns (8 s real timeout, enumerations not logged)", new[] { "init" } },
            { "init-none", new[] { "init" } },
            { "init-ps4", new[] { "init" } },
            { "init-multiple", new[] { "init" } },
            { "init-multiple-with-ps4", new[] { "init" } },
            { "init-g29-and-ps4", new[] { "init" } },
            { "init-open-fails", new[] { "init" } },
            { "init-send-fails", new[] { "init" } },
            { "init-range-39", new[] { "init", "--range", "39" } },
            { "init-autocenter-101", new[] { "init", "--autocenter", "101" } },
            { "range-39", new[] { "range", "39" } },
            { "range-40", new[] { "range", "40" } },
            { "range-541", new[] { "range", "541" } },
            { "range-900", new[] { "range", "900" } },
            { "range-901", new[] { "range", "901" } },
            { "autocenter--1", new[] { "autocenter", "-1" } },
            { "autocenter-0", new[] { "autocenter", "0" } },
            { "autocenter-1", new[] { "autocenter", "1" } },
            { "autocenter-66", new[] { "autocenter", "66" } },
            { "autocenter-67", new[] { "autocenter", "67" } },
            { "autocenter-100", new[] { "autocenter", "100" } },
            { "autocenter-101", new[] { "autocenter", "101" } },
            { "leds--1", new[] { "leds", "-1" } },
            { "leds-0", new[] { "leds", "0" } },
            { "leds-21", new[] { "leds", "21" } },
            { "leds-31", new[] { "leds", "31" } },
            { "leds-32", new[] { "leds", "32" } },
            { "stop", new[] { "stop" } },
            { "range-on-compatibility", new[] { "range", "900" } },
            { "stop-on-compatibility", new[] { "stop" } },
            { "stop-none", new[] { "stop" } },
            { "stop-ps4", new[] { "stop" } },
            { "leds-multiple", new[] { "leds", "1" } }
        };

        private static void ControllerTranscripts()
        {
            IList<string> lines = ReferenceFixtures.Lines("controller.txt");
            int replayed = 0;
            for (int index = 0; index < lines.Count; index++)
            {
                string scenario = lines[index].Substring("scenario ".Length);
                var expected = new List<string>();
                string result;
                while (true)
                {
                    string line = lines[++index];
                    if (line.StartsWith("result ", StringComparison.Ordinal))
                    {
                        result = line.Substring(7);
                        break;
                    }

                    if (!line.StartsWith("enumerate ", StringComparison.Ordinal))
                    {
                        expected.Add(line);
                    }
                }

                string[] arguments;
                if (!ControllerCommands.TryGetValue(scenario, out arguments))
                {
                    // force-* (ApplyForce at the protocol's full range), find-devices and
                    // ps4-connected have no g29ctl equivalent; protocol.txt covers the
                    // force bytes and cli.txt the bounded force command.
                    continue;
                }

                FakeBridge bridge = ControllerWheels(scenario);
                bridge.RunCli(arguments);
                string name = "controller scenario " + scenario;
                Compare(expected, HidEvents(bridge), name);
                if (result.StartsWith("error ", StringComparison.Ordinal))
                {
                    Assert.True(bridge.Stderr == "[THE VOID OBJECTS] " + result.Substring(6).Replace("\\r\\n", "\r\n") + "\r\n", name + ": error message, got " + bridge.Stderr);
                    Assert.True(bridge.ExitCode == 1, name + ": exit 1");
                }
                else
                {
                    Assert.True(bridge.Stderr.Length == 0 && bridge.ExitCode == 0, name + ": success, got " + bridge.Stderr);
                }

                replayed++;
            }

            Assert.True(replayed == ControllerCommands.Count, "every mapped controller scenario replayed");
        }

        private static void CancelAndTiming()
        {
            // Compatibility wheel that never comes back: 8 s of 250 ms polls on the
            // virtual clock, then the timeout error; the first poll is immediate.
            var lost = new FakeBridge(new FakeBridge.Phase(FakeBridge.Compatibility("c1")));
            long start = lost.Clock;
            lost.RunCli(new[] { "init" });
            Assert.True(lost.Stderr.Contains("did not reconnect in native mode"), "re-enumeration timeout");
            long elapsed = lost.Clock - start;
            Assert.True(elapsed >= 8000000 && elapsed < 8250000 + 1, "timeout after 8 s of virtual time, got " + elapsed);
            Assert.Equal(33, lost.Enumerations, "initial find plus one poll per 250 ms up to the 8 s limit");

            // Ctrl+C while the diagnostic force is applied: the force is stopped at once.
            var wheel = new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            wheel.HoldTimer = 1;
            wheel.RunCli(new[] { "force", "-20", "--milliseconds", "5000", "--i-understand" });
            Assert.True(!wheel.ExitCode.HasValue, "force is waiting on its timer");
            wheel.Post(new Frame(0x44, 0, 0, new byte[0]));
            Compare(new List<string>
            {
                "open n1", "send n1 F5000000000000", "close n1",
                "open n1", "send n1 11086780000000", "close n1",
                "timer 1 5000", "timer-cancel 1",
                "open n1", "send n1 13000000000000", "close n1"
            }, AllEvents(wheel), "Ctrl+C during the force test sends the stop report");
            Assert.True(wheel.ExitCode == 0 && wheel.Stdout.StartsWith("FORCE RITUAL COMPLETE", StringComparison.Ordinal), "cancelled force test still completes");

            // Ctrl+C outside the force test ends the command like the legacy tool.
            var init = new FakeBridge(new FakeBridge.Phase(FakeBridge.Compatibility("c1")));
            init.HoldTimer = 0;
            init.RunCli(new[] { "init" });
            Assert.True(!init.ExitCode.HasValue, "init is waiting for the native wheel");
            init.Post(new Frame(0x44, 0, 0, new byte[0]));
            Assert.True(init.ExitCode == 0xC000013A, "Ctrl+C during init exits with STATUS_CONTROL_C_EXIT");
        }

        private static FakeBridge Wheels(string wheel)
        {
            switch (wheel)
            {
                case "none":
                    return new FakeBridge(new FakeBridge.Phase());
                case "compat":
                    var compat = new FakeBridge(new FakeBridge.Phase(FakeBridge.Compatibility("c1")), new FakeBridge.Phase(FakeBridge.Native("n2")));
                    compat.PhaseAfterNativeSwitch = 1;
                    return compat;
                case "ps4":
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Ps4("p1")));
                case "two":
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1"), FakeBridge.Compatibility("c1")));
                default:
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            }
        }

        private static FakeBridge ControllerWheels(string scenario)
        {
            if (scenario.StartsWith("init-compatibility-switches", StringComparison.Ordinal))
            {
                var bridge = new FakeBridge(new FakeBridge.Phase(FakeBridge.Compatibility("c1")), new FakeBridge.Phase(FakeBridge.Native("n2")));
                bridge.PhaseAfterNativeSwitch = 1;
                return bridge;
            }

            if (scenario.StartsWith("init-compatibility-native-appears", StringComparison.Ordinal))
            {
                var bridge = new FakeBridge(new FakeBridge.Phase(FakeBridge.Compatibility("c1")), new FakeBridge.Phase(FakeBridge.Compatibility("c1"), FakeBridge.Native("n2")));
                bridge.PhaseAfterNativeSwitch = 1;
                return bridge;
            }

            if (scenario.StartsWith("init-compatibility-never", StringComparison.Ordinal) || scenario.EndsWith("on-compatibility", StringComparison.Ordinal))
            {
                return new FakeBridge(new FakeBridge.Phase(FakeBridge.Compatibility("c1")));
            }

            switch (scenario)
            {
                case "init-none":
                case "stop-none":
                    return new FakeBridge(new FakeBridge.Phase());
                case "init-ps4":
                case "stop-ps4":
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Ps4("p1")));
                case "init-multiple":
                case "leds-multiple":
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1"), FakeBridge.Native("n2")));
                case "init-multiple-with-ps4":
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1"), FakeBridge.Compatibility("c1"), FakeBridge.Ps4("p1")));
                case "init-g29-and-ps4":
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1"), FakeBridge.Ps4("p1")));
                case "init-open-fails":
                    var open = new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
                    open.FailOpenPath = "n1";
                    return open;
                case "init-send-fails":
                    var send = new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
                    send.FailSendPath = "n1";
                    return send;
                default:
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            }
        }

        private static List<string> HidEvents(FakeBridge bridge)
        {
            var events = new List<string>();
            foreach (string entry in bridge.Log)
            {
                if (entry.StartsWith("abi-error", StringComparison.Ordinal) || entry.StartsWith("log ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The program reported a protocol problem: " + entry);
                }

                if (!entry.StartsWith("timer", StringComparison.Ordinal))
                {
                    events.Add(entry);
                }
            }

            return events;
        }

        private static List<string> AllEvents(FakeBridge bridge)
        {
            return new List<string>(bridge.Log);
        }

        private static List<string> Lines(string text)
        {
            var lines = new List<string>();
            string normalized = text.Replace("\r\n", "\n");
            if (normalized.EndsWith("\n", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            if (normalized.Length > 0)
            {
                lines.AddRange(normalized.Split('\n'));
            }

            return lines;
        }

        private static void Compare(IList<string> expected, IList<string> actual, string name)
        {
            int count = Math.Max(expected.Count, actual.Count);
            for (int index = 0; index < count; index++)
            {
                string want = index < expected.Count ? expected[index] : "<nothing>";
                string got = index < actual.Count ? actual[index] : "<nothing>";
                if (want != got)
                {
                    throw new InvalidOperationException(string.Format("{0}: line {1} expected '{2}', got '{3}'.\nExpected:\n  {4}\nActual:\n  {5}", name, index + 1, want, got, string.Join("\n  ", expected), string.Join("\n  ", actual)));
                }
            }
        }
    }
}
