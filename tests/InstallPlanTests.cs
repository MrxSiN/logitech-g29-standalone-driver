using System.Collections.Generic;

namespace G29.Tests
{
    // "g29ctl install-plan": the installation policy the installer scripts execute.
    internal static class InstallPlanTests
    {
        internal static void Run()
        {
            var bridge = new FakeBridge(new FakeBridge.Phase());
            bridge.RunCli(new[] { "install-plan" });
            Assert.True(bridge.ExitCode == 0, "plan exits 0");
            Assert.True(bridge.Stdout == string.Join("\r\n", new List<string>
            {
                "service G29Standalone",
                "display G29 Standalone // GHUB == NULL",
                "description PLEASE INITIALIZE THE LOGITECH G29 WITHOUT SUMMONING G HUB.",
                "directory G29Standalone",
                "startup Manual",
                "arguments service --range 900 --autocenter 0",
                @"trigger start/device/a5dcbf10-6530-11d2-901f-00c04fb951ed/USB\VID_046D&PID_C24F/USB\VID_046D&PID_C294/USB\VID_046D&PID_C298/USB\VID_046D&PID_C299/USB\VID_046D&PID_C29A/USB\VID_046D&PID_C29B/USB\VID_046D&PID_C260",
                "failure-reset 86400",
                "failure-actions restart/5000/restart/15000",
                "start-after-install yes",
                string.Empty
            }.ToArray()), "the previous installer's values, defaults included");

            bridge = new FakeBridge(new FakeBridge.Phase());
            bridge.RunCli(new[] { "install-plan", "--autocenter", "35", "--range", "40" });
            Assert.True(bridge.Stdout.Contains("arguments service --range 40 --autocenter 35\r\n"), "options become the service arguments");
            Assert.True(bridge.Log.Count == 0, "the plan touches no device");

            foreach (string[] arguments in new[]
            {
                new[] { "install-plan", "--range", "39" },
                new[] { "install-plan", "--range", "901" },
                new[] { "install-plan", "--autocenter", "101" },
                new[] { "install-plan", "--autocenter", "-1" },
                new[] { "install-plan", "--range" },
                new[] { "install-plan", "--leds", "1" }
            })
            {
                bridge = new FakeBridge(new FakeBridge.Phase());
                bridge.RunCli(arguments);
                Assert.True(bridge.ExitCode == 1 && bridge.Stdout.Length == 0 && bridge.Stderr.StartsWith("[THE VOID OBJECTS] "), "refused: " + string.Join(" ", arguments));
            }
        }
    }
}
