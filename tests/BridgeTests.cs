using System;
using System.Collections.Generic;

namespace G29.Tests
{
    // The native bridge's emergency output guard (independent of the Brainfuck
    // program) and its generic shared-memory mechanism.
    internal static class BridgeTests
    {
        internal static void Run()
        {
            Guard();
            SharedMemory();
        }

        private static void SharedMemory()
        {
            // a session-local name: no privileges needed, nothing persists
            string name = @"Local\G29StandaloneTest." + Guid.NewGuid().ToString("N");
            using (var service = new NativeSharedMemory())
            using (var game = new NativeSharedMemory())
            {
                uint missing;
                Assert.Equal(2, game.Open(name, 16, out missing), "opening a mapping nobody created");
                uint created;
                Assert.Equal(0, service.Create(name, 16, true, out created), "create");
                uint opened;
                Assert.Equal(0, game.Open(name, 16, out opened), "open");
                Assert.True(game.Write(opened, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 0, 0 }), "write");
                Assert.True(ReferenceFixtures.Hex(service.Read(created, 0, 12)) == "010203040506070809000000", "both views see the same bytes");
                Assert.True(service.Write(created, 8, new byte[4]), "write back");
                Assert.True(ReferenceFixtures.Hex(game.Read(opened, 8, 4)) == "00000000", "writes go both ways");
                Assert.False(game.Write(opened, 13, new byte[4]), "writes outside the mapping are refused");
                game.Close(opened);
                Assert.True(game.Read(opened, 0, 4) == null, "a closed handle is unknown");
            }
        }

        private static void Guard()
        {
            var writes = new List<string>();
            Func<string, int, byte[], int> writer = delegate(string path, int length, byte[] payload)
            {
                writes.Add(path + " " + length + " " + ReferenceFixtures.Hex(payload));
                return 0;
            };

            using (var cli = new NativeGuard(25, writer))
            {
                Assert.False(cli.Refuses(Force(0x80 + 32)), "25 % ceiling allows the 25 % report");
                Assert.False(cli.Refuses(Force(0x80 - 32)), "25 % ceiling allows -25 %");
                Assert.True(cli.Refuses(Force(0x80 + 33)), "25 % ceiling refuses more");
                Assert.True(cli.Refuses(Force(0x80 - 33)), "25 % ceiling refuses more, negative");
                Assert.True(cli.Refuses(Force(0xFF)), "25 % ceiling refuses full scale");
                Assert.False(cli.Refuses(new byte[] { 0xF8, 0x81, 0x84, 0x03, 0, 0, 0 }), "non-force reports pass");
            }

            using (var game = new NativeGuard(100, writer))
            {
                Assert.True(!game.Refuses(Force(0xFF)) && !game.Refuses(Force(0x01)), "full-scale ceiling allows +-100 %");

                // Tracking and the emergency stop.
                game.Written("wheel-a", 17, Force(0x90));
                game.Written("wheel-b", 7, Force(0x70));
                game.Written("wheel-b", 7, new byte[] { 0x13, 0, 0, 0, 0, 0, 0 });
                game.Written("wheel-c", 7, new byte[] { 0xF8, 0x12, 0, 0, 0, 0, 0 });
                game.Written("wheel-d", 7, Force(0x90));
                game.Written("WHEEL-D", 7, Force(0x80));
                Assert.True(game.AnyForce, "a forced wheel is remembered");
                game.EmergencyStop();
                Assert.Equal(1, writes.Count, "only the wheel still holding force is stopped (a zero force releases it too; paths ignore case)");
                Assert.True(writes[0] == "wheel-a 17 13000000000000", "emergency stop writes the verified stop report");
                Assert.False(game.AnyForce, "nothing remains after the emergency stop");
                game.EmergencyStop();
                Assert.Equal(1, writes.Count, "the emergency stop does not repeat itself");
            }

            using (var failing = new NativeGuard(100, delegate { return 31; }))
            {
                failing.Written("gone", 7, Force(0x90));
                failing.EmergencyStop();
                Assert.False(failing.AnyForce, "a failed emergency write is best effort");
            }
        }

        private static byte[] Force(int value)
        {
            return new byte[] { 0x11, 0x08, (byte)value, 0x80, 0, 0, 0 };
        }
    }
}
