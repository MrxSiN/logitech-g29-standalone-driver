using System;
using System.Collections.Generic;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // The force-feedback crash watchdog in Brainfuck (watchdog.bfa): the rule
    // against the frozen ForceWatchdog vectors (tests/reference/watchdog.txt), and
    // the service's heartbeat mapping, checks and stop against a fake bridge.
    internal static class WatchdogTests
    {
        private const string Mapping = @"Global\G29Standalone.ForceHeartbeat";

        internal static void Run()
        {
            RuleMatchesTheVectors();
            AbandonedDriverIsStopped();
            HealthyOrInactiveHeartbeatIsLeftAlone();
            StopFailureIsLogged();
            UnavailableMapping();
        }

        private static void RuleMatchesTheVectors()
        {
            var harness = new BfHarness(BfMainTests.Program, BfVm.DefaultStepBudget);
            harness.Post(BfMainTests.Boot(4));
            harness.Run();
            ushort sequence = 0;
            int checkedLines = 0;
            foreach (string line in ReferenceFixtures.Lines("watchdog.txt"))
            {
                string[] parts = line.Split(' ');
                long beat = long.Parse(parts[2]);
                long now = long.Parse(parts[3]);
                var payload = new PayloadWriter().U8('H').U8(int.Parse(parts[1]))
                    .U8(beat < 0 ? 1 : 0).U64(Math.Abs(beat))
                    .U8(now < 0 ? 1 : 0).U64(Math.Abs(now));
                sequence++;
                harness.Post(new Frame(0x05, 0, sequence, payload.ToArray()));
                IList<Frame> frames = harness.Run();
                string actual = frames[0].Payload[1].ToString();
                if (actual != parts[5])
                {
                    throw new InvalidOperationException(string.Format("Watchdog vector mismatch: '{0}' produced '{1}'.", line, actual));
                }

                checkedLines++;
            }

            Assert.True(checkedLines > 100, "watchdog fixture replayed");
        }

        // Log entries that start with any of the prefixes, in order.
        private static List<string> Starting(IList<string> log, params string[] prefixes)
        {
            var result = new List<string>();
            foreach (string entry in log)
            {
                foreach (string prefix in prefixes)
                {
                    if (entry.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        result.Add(entry);
                        break;
                    }
                }
            }

            return result;
        }

        private static byte[] Heartbeat(long beat, int active)
        {
            var bytes = new byte[16];
            Array.Copy(BitConverter.GetBytes(beat), 0, bytes, 0, 8);
            Array.Copy(BitConverter.GetBytes(active), 0, bytes, 8, 4);
            return bytes;
        }

        private static FakeBridge Service(byte[] heartbeat, int ticks, params FakeBridge.FakeDevice[] wheels)
        {
            var bridge = new FakeBridge(new FakeBridge.Phase(wheels));
            if (heartbeat != null)
            {
                bridge.SharedMemory[Mapping] = heartbeat;
            }

            bridge.StopAfterTicks = ticks;
            bridge.StopEvent = 0x41;
            bridge.RunCli(new[] { "service" });
            Assert.True(bridge.ExitCode == 0, "service ends normally");
            return bridge;
        }

        private static void AbandonedDriverIsStopped()
        {
            // force marked active, last beat long ago: stop the wheel once
            FakeBridge bridge = Service(Heartbeat(0, 1), 4, FakeBridge.Native("n1"));
            var log = Starting(bridge.Log, "shm-", "timer 3 ", "timer-cancel 3", "send n1 13", "event A DirectInput");
            MonitorParityTests.Compare(new List<string>
            {
                "shm-create " + Mapping + " 16 1",
                "timer 3 250",
                "shm-write 8 00000000",
                "send n1 13000000000000",
                "event A DirectInput client stopped responding while applying force. The stop report was sent.",
                "timer-cancel 3",
                "shm-close"
            }, log, "abandoned force is stopped once, the flag cleared, the mapping closed at the end");
            Assert.True(bridge.SharedMemory[Mapping][8] == 0, "active flag cleared");

            // a negative (corrupt) beat is in the past as well
            bridge = Service(Heartbeat(-1, 1), 2, FakeBridge.Native("n1"));
            Assert.True(Starting(bridge.Log, "send n1 13").Count == 1, "negative beat counts as abandoned");
        }

        private static void HealthyOrInactiveHeartbeatIsLeftAlone()
        {
            // the first check happens 1 s after the service starts (fake clock)
            long firstCheck = 5001000;
            FakeBridge bridge = Service(Heartbeat(firstCheck, 1), 2, FakeBridge.Native("n1"));
            Assert.True(Starting(bridge.Log, "shm-write", "send n1 13").Count == 0, "a fresh beat is not abandoned");
            bridge = Service(Heartbeat(firstCheck - 1000, 1), 2, FakeBridge.Native("n1"));
            Assert.True(Starting(bridge.Log, "shm-write", "send n1 13").Count == 0, "exactly 1000 ms is not abandoned");
            bridge = Service(Heartbeat(firstCheck - 1001, 1), 2, FakeBridge.Native("n1"));
            Assert.True(Starting(bridge.Log, "send n1 13").Count == 1, "1001 ms is abandoned");
            bridge = Service(Heartbeat(0, 0), 4, FakeBridge.Native("n1"));
            Assert.True(Starting(bridge.Log, "shm-write", "send n1 13").Count == 0, "no force marked active");
            bridge = Service(Heartbeat(long.MaxValue, 1), 3, FakeBridge.Native("n1"));
            Assert.True(Starting(bridge.Log, "shm-write", "send n1 13").Count == 0, "a beat in the far future is not abandoned");
        }

        private static void StopFailureIsLogged()
        {
            FakeBridge bridge = Service(Heartbeat(0, 1), 3);
            List<string> events = Starting(bridge.Log, "event A DirectInput");
            Assert.True(events.Count == 1 && events[0].StartsWith("event A DirectInput client stopped responding while applying force, and the stop report failed: ", StringComparison.Ordinal), "the failed stop is logged with the reason");
        }

        private static void UnavailableMapping()
        {
            var bridge = new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            bridge.SharedMemoryCreateError = 5;
            bridge.StopAfterTicks = 3;
            bridge.StopEvent = 0x41;
            bridge.RunCli(new[] { "service" });
            MonitorParityTests.Compare(new List<string>
            {
                "event Force feedback watchdog unavailable (Windows error 5).",
                "event Configured Test G29 (native)."
            }, Starting(bridge.Log, "event "), "the service runs on without its watchdog");
            Assert.True(Starting(bridge.Log, "timer 3 ", "shm-close").Count == 0, "no checks without the mapping");
        }
    }
}
