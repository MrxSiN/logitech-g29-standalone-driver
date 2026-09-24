using System;
using System.Collections.Generic;
using System.Linq;

namespace G29.Tests
{
    // g29ctl ffb-register / ffb-unregister / doctor in Brainfuck against an
    // in-memory registry. Expected values come from the frozen registration data
    // (tests/reference/registration.txt) and the legacy rules recorded there.
    internal static class RegistrationTests
    {
        private const string Dll64 = @"C:\Program Files\G29Standalone\g29ffb64.dll";
        private const string Dll32 = @"C:\Program Files\G29Standalone\g29ffb32.dll";
        private const string Oem = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C24F";
        private const string OurClass = "{D252A2D4-A917-47D3-BD1B-F5A0138CFE12}";

        internal static void Run()
        {
            RegisterOnCleanMachine();
            RegisterBesideExistingOemKeys();
            ForeignDrivers();
            Doctor();
        }

        private static FakeBridge Machine()
        {
            var bridge = new FakeBridge(new FakeBridge.Phase(FakeBridge.Native("n1")));
            bridge.Files[Dll64] = new[] { string.Empty, string.Empty };
            bridge.Files[Dll32] = new[] { string.Empty, string.Empty };
            // keys every Windows installation has
            bridge.Registry.CreateKey(@"HKLM\SOFTWARE\Classes\CLSID");
            bridge.Registry.CreateKey(@"HKLM32\SOFTWARE\Classes\CLSID");
            bridge.Registry.CreateKey(@"HKLM\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM");
            bridge.Registry.CreateKey(@"HKCU\Software");
            bridge.Registry.CreateKey(@"HKCU\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM");
            return bridge;
        }

        private static Dictionary<string, string> Frozen()
        {
            var values = new Dictionary<string, string>();
            foreach (string line in ReferenceFixtures.Lines("registration.txt"))
            {
                string[] parts = line.Split(new[] { ' ' }, 3);
                if (parts[0] == "value" || parts[0] == "blob")
                {
                    values[parts[1]] = parts[2];
                }
            }

            return values;
        }

        // The registry a successful registration must produce, built from the
        // frozen data.
        private static List<string> ExpectedRegistration(string[] oemRoots, bool createdOem, bool createdAxis)
        {
            Dictionary<string, string> frozen = Frozen();
            var expected = new FakeRegistry();
            string classKey = frozen["ClassKeyPath"];
            // Phase 15: the class is served by the native DLL of each view (the
            // frozen data describes the previous .NET registration of the same class).
            foreach (string hive in new[] { "HKLM", "HKLM32" })
            {
                expected.Set(hive + @"\" + classKey, string.Empty, 1, frozen["ClassDisplayName"]);
                expected.Set(hive + @"\" + classKey + @"\InprocServer32", string.Empty, 1, hive == "HKLM" ? Dll64 : Dll32);
                expected.Set(hive + @"\" + classKey + @"\InprocServer32", "ThreadingModel", 1, frozen["ThreadingModel"]);
            }

            foreach (string root in oemRoots)
            {
                string oem = root + @"\" + frozen["OemKeyPath"];
                string markers = root + @"\" + frozen["MarkerKeyPath"];
                expected.CreateKey(markers);
                if (createdOem)
                {
                    expected.Set(markers, frozen["CreatedOemKeyMarker"], 2, 1L);
                }

                if (createdAxis)
                {
                    expected.Set(markers, frozen["CreatedAxisMarker"], 2, 1L);
                    expected.Set(oem + @"\Axes\0", string.Empty, 1, frozen["AxisDisplayName"]);
                    expected.Set(oem + @"\Axes\0", "Attributes", 3, ReferenceFixtures.Hex(frozen["SteeringAxisAttributes"]));
                    expected.Set(oem + @"\Axes\0", "FFAttributes", 3, ReferenceFixtures.Hex(frozen["SteeringAxisForceAttributes"]));
                }

                string ff = oem + @"\" + frozen["ForceFeedbackKeyName"];
                expected.Set(ff, "CLSID", 1, frozen["ClassIdString"]);
                expected.Set(ff, "Attributes", 3, ReferenceFixtures.Hex(frozen["DeviceAttributes"]));
                foreach (string line in ReferenceFixtures.Lines("registration.txt"))
                {
                    string[] parts = line.Split(' ');
                    if (parts[0] == "effect")
                    {
                        expected.Set(ff + @"\Effects\" + parts[1], string.Empty, 1, parts[2].Replace('_', ' '));
                        expected.Set(ff + @"\Effects\" + parts[1], "Attributes", 3, ReferenceFixtures.Hex(parts[3]));
                    }
                }
            }

            return expected.Dump();
        }

        private static void RegisterOnCleanMachine()
        {
            FakeBridge machine = Machine();
            List<string> before = machine.Registry.Dump();
            machine.RunCli(new[] { "ffb-register" });
            Assert.True(machine.ExitCode == 0 && machine.Stdout == "THE FORCE SPIRIT HAS BEEN BOUND TO DIRECTINPUT.\r\n", "ffb-register succeeds: " + machine.Stderr);
            List<string> wanted = Merge(before, ExpectedRegistration(new[] { "HKLM", "HKCU" }, true, true));
            Compare(wanted, machine.Registry.Dump(), "registration on a clean machine");

            // Registering again replaces our own registration and changes nothing.
            FakeBridge again = Machine();
            again.Registry = machine.Registry;
            again.RunCli(new[] { "ffb-register" });
            Assert.True(again.ExitCode == 0, "registration can be repeated");
            Compare(wanted, again.Registry.Dump(), "repeated registration");

            // An upgrade over the previous .NET registration of the class leaves
            // none of its values behind.
            FakeBridge upgrade = Machine();
            foreach (string hive in new[] { "HKLM", "HKLM32" })
            {
                string server = hive + @"\SOFTWARE\Classes\CLSID\" + OurClass + @"\InprocServer32";
                upgrade.Registry.Set(server, string.Empty, 1, "mscoree.dll");
                upgrade.Registry.Set(server, "CodeBase", 1, "file:///C:/Program Files/G29Standalone/g29ffb.dll");
                upgrade.Registry.Set(server + @"\1.0.0.0", "Class", 1, "G29.Windows.ForceFeedback.G29EffectDriver");
            }

            upgrade.RunCli(new[] { "ffb-register" });
            Assert.True(upgrade.ExitCode == 0, "registration over the .NET driver succeeds");
            Compare(wanted, upgrade.Registry.Dump(), "registration over the .NET driver");

            FakeBridge remove = Machine();
            remove.Registry = machine.Registry;
            remove.RunCli(new[] { "ffb-unregister" });
            Assert.True(remove.ExitCode == 0 && remove.Stdout == "THE FORCE SPIRIT HAS BEEN RELEASED FROM DIRECTINPUT.\r\n", "ffb-unregister succeeds");
            Compare(before, remove.Registry.Dump(), "unregistration restores the machine exactly");

            FakeBridge missing = Machine();
            missing.Files.Clear();
            missing.RunCli(new[] { "ffb-register" });
            Assert.True(missing.ExitCode == 1 && missing.Stderr == "[THE VOID OBJECTS] The force feedback driver was not found.\r\n", "missing driver file");
            Assert.Equal(0, missing.RegistryRequests, "nothing is registered without the driver file");
        }

        // DirectInput already created the OEM key and its axis: registration adds
        // only OEMForceFeedback, and unregistration leaves DirectInput's keys.
        private static void RegisterBesideExistingOemKeys()
        {
            FakeBridge machine = Machine();
            foreach (string root in new[] { "HKLM", "HKCU" })
            {
                machine.Registry.Set(root + @"\" + Oem, "OEMName", 1, "Logitech G29 Driving Force Racing Wheel USB");
                machine.Registry.Set(root + @"\" + Oem + @"\Axes\0", string.Empty, 1, "Wheel axis");
                machine.Registry.Set(root + @"\" + Oem + @"\Axes\0", "Attributes", 3, new byte[] { 1, 0x81, 0, 0 });
            }

            List<string> before = machine.Registry.Dump();
            machine.RunCli(new[] { "ffb-register" });
            Assert.True(machine.ExitCode == 0, "registration beside existing keys");
            foreach (string root in new[] { "HKLM", "HKCU" })
            {
                Assert.True(machine.Registry.Get(root + @"\Software\G29Standalone\DirectInput", "CreatedOemKey") == null, "OEM key not marked as ours");
                Assert.True(machine.Registry.Get(root + @"\Software\G29Standalone\DirectInput", "CreatedSteeringAxis") == null, "axis not marked as ours");
                Assert.True(machine.Registry.Get(root + @"\" + Oem + @"\Axes\0", "FFAttributes") == null, "an existing axis is not modified");
                Assert.True(((string)machine.Registry.Get(root + @"\" + Oem + @"\OEMForceFeedback", "CLSID").Data) == OurClass, "OEMForceFeedback registered");
            }

            FakeBridge remove = Machine();
            remove.Registry = machine.Registry;
            remove.RunCli(new[] { "ffb-unregister" });
            Compare(before, remove.Registry.Dump(), "unregistration keeps DirectInput's own keys");
        }

        private static void ForeignDrivers()
        {
            // A foreign registration whose class is gone (an uninstalled G HUB) is replaced.
            FakeBridge orphan = Machine();
            orphan.Registry.Set(@"HKLM\" + Oem + @"\OEMForceFeedback", "CLSID", 1, "{12345678-1234-1234-1234-123456789ABC}");
            orphan.Registry.Set(@"HKLM\" + Oem + @"\OEMForceFeedback\Effects\{old}", string.Empty, 1, "old effect");
            orphan.RunCli(new[] { "ffb-register" });
            Assert.True(orphan.ExitCode == 0, "an orphaned registration is replaced");
            Assert.False(orphan.Registry.Exists(@"HKLM\" + Oem + @"\OEMForceFeedback\Effects\{old}"), "the orphaned registration's keys are gone");

            // A working foreign driver (its class is still installed) stops the command.
            foreach (string classHive in new[] { "HKLM", "HKLM32" })
            {
                FakeBridge foreign = Machine();
                foreign.Registry.Set(@"HKCU\" + Oem + @"\OEMForceFeedback", "CLSID", 1, "{AAAAAAAA-1234-1234-1234-123456789ABC}");
                foreign.Registry.Set(classHive + @"\SOFTWARE\Classes\CLSID\{AAAAAAAA-1234-1234-1234-123456789ABC}\InprocServer32", string.Empty, 1, @"C:\Other\ffb.dll");
                foreign.RunCli(new[] { "ffb-register" });
                Assert.True(foreign.ExitCode == 1 && foreign.Stderr == "[THE VOID OBJECTS] Another force feedback driver ({AAAAAAAA-1234-1234-1234-123456789ABC}) is registered for the G29 in HKEY_CURRENT_USER. Remove it before installing this one.\r\n", "a working foreign driver is refused (" + classHive + "): " + foreign.Stderr);
                Assert.True(((string)foreign.Registry.Get(@"HKCU\" + Oem + @"\OEMForceFeedback", "CLSID").Data).StartsWith("{AAAA", StringComparison.Ordinal), "the foreign registration is untouched");
            }

            // Our own class ID in any letter case counts as ours.
            FakeBridge lower = Machine();
            lower.Registry.Set(@"HKLM\" + Oem + @"\OEMForceFeedback", "CLSID", 1, OurClass.ToLowerInvariant());
            lower.Registry.Set(@"HKLM\SOFTWARE\Classes\CLSID\" + OurClass.ToLowerInvariant() + @"\InprocServer32", string.Empty, 1, "mscoree.dll");
            lower.RunCli(new[] { "ffb-register" });
            Assert.True(lower.ExitCode == 0, "our class ID is recognized ignoring case");
        }

        private static void Doctor()
        {
            FakeBridge absent = Machine();
            absent.RunCli(new[] { "doctor" });
            Compare(new List<string>
            {
                "Test G29 | VID 046D PID C24F REV 8900 | native | HID 01:04, reports in/out 13/17",
                "G HUB processes: none running",
                "DirectInput OEM force feedback: THE ANCIENT REGISTRATION IS ABSENT",
                "Axes/buttons may still live; game-driven force effects may remain trapped beyond the veil."
            }, Lines(absent.Stdout), "doctor without registration");
            Assert.True(absent.ExitCode == 0, "doctor exit code follows status");

            FakeBridge registered = Machine();
            registered.RunCli(new[] { "ffb-register" });
            FakeBridge doctor = Machine();
            doctor.Registry = registered.Registry;
            doctor.Processes.Add(new KeyValuePair<int, string>(100, "explorer"));
            doctor.Processes.Add(new KeyValuePair<int, string>(200, "LGHUB"));
            doctor.Processes.Add(new KeyValuePair<int, string>(300, "lghub_agent"));
            doctor.Processes.Add(new KeyValuePair<int, string>(50, "lghub"));
            doctor.Processes.Add(new KeyValuePair<int, string>(2147483, "lghub_software_manager"));
            doctor.Processes.Add(new KeyValuePair<int, string>(7, "lghubx"));
            doctor.RunCli(new[] { "doctor" });
            Compare(new List<string>
            {
                "Test G29 | VID 046D PID C24F REV 8900 | native | HID 01:04, reports in/out 13/17",
                "G HUB processes: LGHUB (PID 200), lghub (PID 50), lghub_agent (PID 300), lghub_software_manager (PID 2147483)",
                "DirectInput OEM force feedback: THE ANCIENT REGISTRATION EXISTS",
                "  Registered in: HKEY_CURRENT_USER",
                "  CLSID: " + OurClass,
                "  64-bit driver: " + Dll64,
                "  32-bit driver: " + Dll32
            }, Lines(doctor.Stdout), "doctor with our registration and G HUB running");

            FakeBridge ghost = new FakeBridge(new FakeBridge.Phase());
            ghost.Registry.Set(@"HKLM\" + Oem + @"\OEMForceFeedback", "CLSID", 1, "{12345678-1234-1234-1234-123456789ABC}");
            ghost.RunCli(new[] { "doctor" });
            Compare(new List<string>
            {
                "THE ORACLE SEES NO LOGITECH G29. Check USB + PS3 mode.",
                "G HUB processes: none running",
                "DirectInput OEM force feedback: A GHOST REGISTRATION HAUNTS HKEY_LOCAL_MACHINE",
                "  CLSID {12345678-1234-1234-1234-123456789ABC} points to a driver that is no longer installed (for example an uninstalled G HUB).",
                "  Run Install-Driver.ps1 to replace it with the G29Standalone driver."
            }, Lines(ghost.Stdout), "doctor with an orphaned registration");
            Assert.True(ghost.ExitCode == 2, "doctor without a wheel exits 2");

            FakeBridge native = Machine();
            native.Registry.Set(@"HKCU\" + Oem + @"\OEMForceFeedback", "CLSID", 1, "{BBBBBBBB-1234-1234-1234-123456789ABC}");
            native.Registry.Set(@"HKLM\SOFTWARE\Classes\CLSID\{BBBBBBBB-1234-1234-1234-123456789ABC}\InprocServer32", string.Empty, 1, @"C:\Vendor\ffb64.dll");
            native.RunCli(new[] { "doctor" });
            List<string> lines = Lines(native.Stdout);
            Assert.True(lines.Contains("  64-bit driver: C:\\Vendor\\ffb64.dll") && lines.Contains("  32-bit driver: not registered (32-bit games get no force feedback)"), "a native 64-bit-only driver: " + string.Join(" | ", lines));
        }

        private static List<string> Merge(List<string> first, List<string> second)
        {
            var registry = new FakeRegistry();
            foreach (List<string> dump in new[] { first, second })
            {
                string key = null;
                foreach (string line in dump)
                {
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        key = line.Substring(1, line.Length - 2);
                        registry.CreateKey(key);
                    }
                    else
                    {
                        int equals = line.IndexOf(" = ", StringComparison.Ordinal);
                        string name = line.Substring(2, equals - 2);
                        string value = line.Substring(equals + 3);
                        int colon = value.IndexOf(':');
                        string kind = value.Substring(0, colon);
                        string data = value.Substring(colon + 1);
                        registry.Set(key, name == "@" ? string.Empty : name, kind == "sz" ? 1 : kind == "dword" ? 2 : 3, kind == "sz" ? (object)data : kind == "dword" ? (object)long.Parse(data) : ReferenceFixtures.Hex(data));
                    }
                }
            }

            return registry.Dump();
        }

        private static List<string> Lines(string text)
        {
            return text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
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
