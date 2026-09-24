using System;
using System.Collections.Generic;
using System.Globalization;

namespace G29.Tests
{
    // The program's device selection against the frozen legacy HidWheelDiscovery
    // filter (tests/reference/selection.txt), through "g29ctl status" with
    // simulated HID interfaces: every interface the program keeps as a G29 is
    // listed by name; PS4-mode wheels are reported when no G29 is kept.
    internal static class SelectionParityTests
    {
        private const int Batch = 64;

        internal static void Run()
        {
            var vectors = new List<Vector>();
            foreach (string line in ReferenceFixtures.Lines("selection.txt"))
            {
                string[] parts = line.Split(' ');
                vectors.Add(new Vector
                {
                    Name = "sel-" + vectors.Count.ToString(CultureInfo.InvariantCulture),
                    Vendor = ReferenceFixtures.HexInt(parts[1]),
                    Product = ReferenceFixtures.HexInt(parts[2]),
                    Revision = ReferenceFixtures.HexInt(parts[3]),
                    UsagePage = ReferenceFixtures.HexInt(parts[4]),
                    Usage = ReferenceFixtures.HexInt(parts[5]),
                    G29 = parts[6] == "1",
                    Ps4 = parts[7] == "1"
                });
            }

            Assert.True(vectors.Count > 5000, "selection fixture loaded");

            // One enumeration of every vector: exactly the legacy G29s are listed.
            FakeBridge all = Status(vectors);
            var listed = new HashSet<string>();
            foreach (string line in all.Stdout.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("sel-", StringComparison.Ordinal))
                {
                    listed.Add(line.Substring(0, line.IndexOf(' ')));
                }
            }

            foreach (Vector vector in vectors)
            {
                if (listed.Contains(vector.Name) != vector.G29)
                {
                    throw new InvalidOperationException(string.Format("Selection mismatch for {0:X4} {1:X4} {2:X4} {3:X4} {4:X4}: G29 expected {5}.", vector.Vendor, vector.Product, vector.Revision, vector.UsagePage, vector.Usage, vector.G29));
                }
            }

            // PS4 mode: batches without a G29 report PS4 mode exactly when they hold
            // a legacy PS4-mode wheel; such batches are checked one vector at a time.
            var others = vectors.FindAll(delegate(Vector vector) { return !vector.G29; });
            int checkedAlone = 0;
            for (int start = 0; start < others.Count; start += Batch)
            {
                List<Vector> batch = others.GetRange(start, Math.Min(Batch, others.Count - start));
                bool expected = batch.Exists(delegate(Vector vector) { return vector.Ps4; });
                Assert.True(ReportsPs4(batch) == expected, "PS4-mode detection in batch " + start);
                if (expected)
                {
                    foreach (Vector vector in batch)
                    {
                        Assert.True(ReportsPs4(new List<Vector> { vector }) == vector.Ps4, "PS4-mode detection of " + vector.Name);
                        checkedAlone++;
                    }
                }
            }

            Assert.True(checkedAlone > 0, "PS4-mode vectors were checked individually");
        }

        private static bool ReportsPs4(List<Vector> vectors)
        {
            FakeBridge bridge = Status(vectors);
            Assert.True(bridge.ExitCode == 2, "no G29 among vectors the legacy filter rejects");
            return bridge.Stdout.Contains("PS4 mode");
        }

        private static FakeBridge Status(List<Vector> vectors)
        {
            var devices = new List<FakeBridge.FakeDevice>();
            foreach (Vector vector in vectors)
            {
                devices.Add(new FakeBridge.FakeDevice(vector.Name, vector.Name, vector.Vendor, vector.Product, vector.Revision, vector.UsagePage, vector.Usage, 13, 17));
            }

            var bridge = new FakeBridge(new FakeBridge.Phase(devices.ToArray()));
            bridge.RunCli(new[] { "status" });
            return bridge;
        }

        private sealed class Vector
        {
            internal string Name;
            internal int Vendor;
            internal int Product;
            internal int Revision;
            internal int UsagePage;
            internal int Usage;
            internal bool G29;
            internal bool Ps4;
        }
    }
}
