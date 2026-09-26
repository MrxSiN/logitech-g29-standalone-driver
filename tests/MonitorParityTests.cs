using System;
using System.Collections.Generic;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The Brainfuck monitor (g29ctl watch and the service) against the frozen
    // WheelMonitor transcripts (tests/reference/monitor.txt), plus the service
    // behavior the legacy fixtures could not capture (15 s idle, stop requests).
    internal static class MonitorParityTests
    {
        internal static void Run()
        {
            WatchTranscripts();
            ServiceIdleAndStop();
            WatchArguments();
        }

        private static void WatchTranscripts()
        {
            IList<string> lines = ReferenceFixtures.Lines("monitor.txt");
            int replayed = 0;
            for (int index = 0; index < lines.Count; index++)
            {
                string[] header = lines[index].Split(' ');
                string scenario = header[1];
                string range = header[2];
                string autoCenter = header[3];
                string idle = header[4];
                var expected = new List<string>();
                while (true)
                {
                    string line = lines[++index];
                    if (line == "return")
                    {
                        break;
                    }

                    expected.Add(line);
                }

                if (idle != "infinite")
                {
                    // Idle timeouts other than the service's fixed 15 s cannot be
                    // selected in either program; ServiceIdleAndStop covers idling.
                    continue;
                }

                int ticks = 0;
                foreach (string line in expected)
                {
                    if (line == "tick")
                    {
                        ticks++;
                    }
                }

                FakeBridge bridge = Wheels(scenario);
                bridge.LogConsoleLines = true;
                bridge.StopAfterTicks = ticks;
                bridge.StopEvent = 0x44;
                bridge.RunCli(new[] { "watch", "--range", range, "--autocenter", autoCenter });
                var actual = new List<string>();
                foreach (string entry in bridge.Log)
                {
                    if (entry.StartsWith("timer", StringComparison.Ordinal) || entry == "log Watching for a G29. Press Ctrl+C to stop.")
                    {
                        continue;
                    }

                    actual.Add(entry);
                }

                Compare(expected, actual, "monitor scenario " + scenario);
                Assert.True(bridge.ExitCode == 0, "watch ends with exit code 0 after Ctrl+C (" + scenario + ")");
                Assert.True(bridge.Log.Contains("timer-cancel 2"), "the pending monitor timer is cancelled (" + scenario + ")");
                replayed++;
            }

            Assert.True(replayed >= 8, "monitor transcripts replayed");
        }

        private static void ServiceIdleAndStop()
        {
            // No wheel: the service checks every second and goes idle once 15 s
            // have passed since it started, then asks the host to stop it.
            var idle = new FakeBridge(new FakeBridge.Phase());
            idle.RunCli(new[] { "service", "--range", "540", "--autocenter", "5" });
            var events = Filter(idle.Log, "service-run", "event", "stop-self");
            Compare(new List<string>
            {
                "service-run G29Standalone",
                "event No configurable G29 connected. Monitor is going idle.",
                "stop-self"
            }, events, "service idles");
            Assert.True(idle.ExitCode == 0, "idle service exits 0");
            Assert.Equal(15, Count(idle.Log, "timer 2 1000"), "fifteen one-second checks before idling");

            // A PS4-mode wheel does not count as configurable: reported once, then idle.
            var ps4 = new FakeBridge(new FakeBridge.Phase(FakeBridge.Ps4("p1")));
            ps4.RunCli(new[] { "service" });
            Compare(new List<string>
            {
                "service-run G29Standalone",
                "event The G29 is in PS4 mode (PID C260), which this tool cannot configure. Set the selector to PS3 and reconnect USB.",
                "event No configurable G29 connected. Monitor is going idle.",
                "stop-self"
            }, Filter(ps4.Log, "service-run", "event", "stop-self"), "PS4 wheel is reported once and the service idles");

            // A wheel keeps the service alive; a stop request ends it at the next wait.
            var wheel = new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            wheel.StopAfterTicks = 40;
            wheel.StopEvent = 0x41;
            wheel.RunCli(new[] { "service", "--range", "900" });
            Compare(new List<string>
            {
                "service-run G29Standalone",
                "open n1", "send n1 F8818403000000", "close n1",
                "open n1", "send n1 F5000000000000", "close n1",
                "open n1", "send n1 F8120000000000", "close n1",
                "event Configured Test G29 (native)."
            }, Filter(wheel.Log, "service-run", "event", "stop-self", "open", "send", "close"), "service configures the wheel once and stays up");
            Assert.True(wheel.ExitCode == 0 && Count(wheel.Log, "tick") == 40, "service runs until stopped");

            // Configuration failures are logged once per distinct message, through the
            // same message text as the command line.
            var bad = new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            bad.StopAfterTicks = 3;
            bad.StopEvent = 0x41;
            bad.RunCli(new[] { "service", "--range", "1000" });
            Compare(new List<string>
            {
                "service-run G29Standalone",
                "event G29 configuration failed: Value must be between 40 and 900.\r\nParameter name: degrees\r\nActual value was 1000."
            }, Filter(bad.Log, "service-run", "event"), "an invalid range is reported once, not every second");

            // Service argument errors end the process before it connects to the SCM.
            var args = new FakeBridge(new FakeBridge.Phase());
            args.RunCli(new[] { "service", "--bogus", "1" });
            Assert.True(args.ExitCode == 1 && args.Stderr == "Error: Unknown option '--bogus'.\r\n" && Filter(args.Log, "service-run").Count == 0, "service rejects unknown options");
        }

        private static void WatchArguments()
        {
            var bridge = new FakeBridge(new FakeBridge.Phase());
            bridge.RunCli(new[] { "watch", "--range" });
            Assert.True(bridge.ExitCode == 1 && bridge.Stderr == "Error: A numeric --range value is required.\r\n", "watch validates its options");
        }

        private static FakeBridge Wheels(string scenario)
        {
            switch (scenario)
            {
                case "reconnect-same-path":
                    return new FakeBridge(
                        new FakeBridge.Phase(FakeBridge.Native("n1")),
                        new FakeBridge.Phase(FakeBridge.Native("n1")),
                        new FakeBridge.Phase(),
                        new FakeBridge.Phase(FakeBridge.Native("n1")),
                        new FakeBridge.Phase(FakeBridge.Native("n1")));
                case "path-change":
                    return new FakeBridge(
                        new FakeBridge.Phase(FakeBridge.Native("n1")),
                        new FakeBridge.Phase(FakeBridge.Native("N1")),
                        new FakeBridge.Phase(FakeBridge.Native("n2")));
                case "ps4-reported-once":
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Ps4("p1")));
                case "ps4-then-ps3":
                    return new FakeBridge(
                        new FakeBridge.Phase(FakeBridge.Ps4("p1")),
                        new FakeBridge.Phase(),
                        new FakeBridge.Phase(FakeBridge.Native("n1")));
                case "multiple-wheels-ignored":
                    return new FakeBridge(
                        new FakeBridge.Phase(FakeBridge.Native("n1"), FakeBridge.Native("n2")),
                        new FakeBridge.Phase(FakeBridge.Native("n1")));
                case "compatibility-wheel":
                    var compat = new FakeBridge(new FakeBridge.Phase(FakeBridge.Compatibility("c1")), new FakeBridge.Phase(FakeBridge.Native("n2")));
                    compat.PhaseAfterNativeSwitch = 1;
                    return compat;
                case "failure-logged-once-then-recovers":
                    var failing = new FakeBridge(
                        new FakeBridge.Phase(FakeBridge.Native("n1")),
                        new FakeBridge.Phase(FakeBridge.Native("n1")),
                        new FakeBridge.Phase(FakeBridge.Native("n1")),
                        new FakeBridge.Phase(FakeBridge.Native("n2")));
                    failing.FailSendPath = "n1";
                    return failing;
                default:
                    return new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            }
        }

        internal static List<string> Filter(IList<string> log, params string[] prefixes)
        {
            var result = new List<string>();
            foreach (string entry in log)
            {
                foreach (string prefix in prefixes)
                {
                    if (entry == prefix || entry.StartsWith(prefix + " ", StringComparison.Ordinal))
                    {
                        result.Add(entry);
                        break;
                    }
                }
            }

            return result;
        }

        private static int Count(IList<string> log, string entry)
        {
            int count = 0;
            foreach (string item in log)
            {
                if (item == entry)
                {
                    count++;
                }
            }

            return count;
        }

        internal static void Compare(IList<string> expected, IList<string> actual, string name)
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
