// PCI unbind safety: never start an nvidia unbind while /dev/nvidia* is held
// (the kernel remove would block forever), and never mistake a stuck unbind
// for a completed one.

using GHelper.Linux.Gpu;
using GHelper.Linux.Gpu.NVidia;

namespace GHelper.Linux.Tests;

public static class UnbindSafetyTests
{
    private static NvidiaHolder Holder(int pid, string comm, int fds = 0, int dri = 0, int i2c = 0, int libs = 0) =>
        new(pid, comm, "ani", fds, libs, true, "", dri, i2c);

    private static string DgpuFunction(Harness.Sandbox sb, string bdf, bool driver)
    {
        string dev = Path.Combine(sb.TempRoot, "sys", "bus", "pci", "devices", bdf);
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "vendor"), "0x10de\n");
        if (driver)
            Directory.CreateDirectory(Path.Combine(dev, "driver"));
        return dev;
    }

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- PCI unbind safety ---");

        Harness.Scenario("UnbindBlockers_NvidiaAndI2cFdsBlock_DriAndLibsDoNot", _ =>
        {
            var blockers = GPUModeControl.SelectUnbindBlockers(new[]
            {
                Holder(5670, "xdg-desktop-por", fds: 12, dri: 2),
                Holder(4198, "gnome-shell", dri: 1),
                Holder(4461, "Xwayland"),
                Holder(7000, "powerdevil", i2c: 1),
                Holder(7100, "steam", libs: 1),
                Holder(5670, "xdg-desktop-por", fds: 12), // duplicate pid from a second list
            });
            Harness.AssertEqual(2, blockers.Count, "blocker count");
            Harness.Assert(blockers.Any(b => b.Pid == 5670), "portal with /dev/nvidia fds blocks");
            Harness.Assert(blockers.Any(b => b.Pid == 7000), "nvidia I2C fd blocks");
        });

        Harness.Scenario("UnbindSafe_SystemHolderKeepsFds_ReturnsFalse", _ =>
        {
            GPUModeControl.UnbindHolderScanForTest = () => new[] { Holder(5862, "gjs", fds: 12) };
            Harness.Assert(!GPUModeControl.WaitForUnbindSafe(timeoutMs: 20, stepMs: 10), "unsafe while gjs holds /dev/nvidia*");
        });

        Harness.Scenario("UnbindSafe_HolderReleasesWithinTimeout_ReturnsTrue", _ =>
        {
            int scans = 0;
            GPUModeControl.UnbindHolderScanForTest = () =>
                ++scans < 3 ? new[] { Holder(5670, "xdg-desktop-por", fds: 12) } : Array.Empty<NvidiaHolder>();
            Harness.Assert(GPUModeControl.WaitForUnbindSafe(timeoutMs: 1000, stepMs: 10), "safe once the portal closes its fds");
        });

        Harness.Scenario("UnbindSafe_OnlyDriFds_ReturnsTrue", _ =>
        {
            GPUModeControl.UnbindHolderScanForTest = () => new[] { Holder(4198, "gnome-shell", dri: 2) };
            Harness.Assert(GPUModeControl.WaitForUnbindSafe(timeoutMs: 20, stepMs: 10), "DRM-only holder does not block unbind");
        });

        Harness.Scenario("Release_HeldBySystemProcess_AbortsBeforeAnyUnbind", sb =>
        {
            DgpuFunction(sb, "0000:01:00.0", driver: true);
            DgpuFunction(sb, "0000:01:00.1", driver: true);
            var actions = new List<string>();
            GPUModeControl.PciActionForTest = (action, driver, bdf) => { actions.Add($"{action} {driver} {bdf}"); return true; };
            GPUModeControl.UnbindHolderScanForTest = () => new[]
            {
                Holder(5670, "xdg-desktop-por", fds: 12, dri: 2),
                Holder(5862, "gjs", fds: 12, dri: 2),
            };

            bool released = GPUModeControl.ReleaseNvidiaModulesAndPurgeHolders("0000:01:00.0", out var stack);
            Harness.Assert(!released, "release refused");
            Harness.AssertEqual(0, stack.Count, "nothing unbound");
            Harness.AssertEqual(0, actions.Count, "no pci-unbind/pci-bind issued: " + string.Join("; ", actions));
            Harness.Assert(!GPUModeControl.DgpuUnbindWedged, "not marked wedged");
        });

        Harness.Scenario("Unbind_TimeoutHelperStillBlocked_DriverLinkGone_IsWedged", sb =>
        {
            // Regression: the kernel drops the driver symlink before nvidia's
            // remove() starts waiting, so a vanished link must not count as done.
            DgpuFunction(sb, "0000:01:00.0", driver: false);
            GPUModeControl.PciActionForTest = (_, _, _) => false; // sudo timeout
            GPUModeControl.PciActionInFlightForTest = (action, bdf) => action == "pci-unbind" && bdf == "0000:01:00.0";
            var outcome = GPUModeControl.TryUnbindFunction(new GPUModeControl.UnbindRecord("0000:01:00.0", "nvidia"), lateMaxMs: 30, stepMs: 10);
            Harness.AssertEqual(GPUModeControl.UnbindOutcome.Wedged, outcome, "outcome");
        });

        Harness.Scenario("Unbind_TimeoutThenHelperExits_DriverGone_IsDone", sb =>
        {
            DgpuFunction(sb, "0000:01:00.0", driver: false);
            int checks = 0;
            GPUModeControl.PciActionForTest = (_, _, _) => false;
            GPUModeControl.PciActionInFlightForTest = (_, _) => ++checks < 3;
            var outcome = GPUModeControl.TryUnbindFunction(new GPUModeControl.UnbindRecord("0000:01:00.0", "nvidia"), lateMaxMs: 1000, stepMs: 10);
            Harness.AssertEqual(GPUModeControl.UnbindOutcome.Done, outcome, "outcome");
        });

        Harness.Scenario("Unbind_HelperFailed_DriverStillBound_IsFailed", sb =>
        {
            DgpuFunction(sb, "0000:01:00.0", driver: true);
            GPUModeControl.PciActionForTest = (_, _, _) => false;
            GPUModeControl.PciActionInFlightForTest = (_, _) => false;
            var outcome = GPUModeControl.TryUnbindFunction(new GPUModeControl.UnbindRecord("0000:01:00.0", "nvidia"), lateMaxMs: 1000, stepMs: 10);
            Harness.AssertEqual(GPUModeControl.UnbindOutcome.Failed, outcome, "outcome");
        });

        Harness.Scenario("Release_UnbindWedged_NoRebindOfStuckFunction_AudioRestored", sb =>
        {
            DgpuFunction(sb, "0000:01:00.0", driver: true);
            DgpuFunction(sb, "0000:01:00.1", driver: true);
            // Driver directories for the rollback existence check.
            Directory.CreateDirectory(Path.Combine(sb.TempRoot, "sys", "bus", "pci", "drivers", "nvidia"));
            Directory.CreateDirectory(Path.Combine(sb.TempRoot, "sys", "bus", "pci", "drivers", "snd_hda_intel"));
            // EnumerateDgpuFunctions resolves driver names from the symlink; replace dirs with links.
            foreach (var (bdf, drv) in new[] { ("0000:01:00.0", "nvidia"), ("0000:01:00.1", "snd_hda_intel") })
            {
                string link = Path.Combine(sb.TempRoot, "sys", "bus", "pci", "devices", bdf, "driver");
                Directory.Delete(link);
                Directory.CreateSymbolicLink(link, Path.Combine(sb.TempRoot, "sys", "bus", "pci", "drivers", drv));
            }

            var actions = new List<string>();
            GPUModeControl.UnbindHolderScanForTest = () => Array.Empty<NvidiaHolder>();
            GPUModeControl.PciActionForTest = (action, driver, bdf) =>
            {
                actions.Add($"{action} {driver} {bdf}");
                if (action == "pci-unbind" && bdf.EndsWith(".1"))
                    return true; // audio unbinds fine
                return action != "pci-unbind"; // graphics unbind times out
            };
            GPUModeControl.PciActionInFlightForTest = (action, bdf) => action == "pci-unbind" && bdf == "0000:01:00.0";

            GPUModeControl.UnbindLateMaxMs = 30;
            GPUModeControl.UnbindLateStepMs = 10;

            bool released = GPUModeControl.ReleaseNvidiaModulesAndPurgeHolders("0000:01:00.0", out var stack);
            Harness.Assert(!released, "release failed");
            Harness.AssertEqual(0, stack.Count, "no unbind records left");
            Harness.Assert(GPUModeControl.DgpuUnbindWedged, "marked wedged");
            Harness.Assert(!actions.Contains("pci-bind nvidia 0000:01:00.0"), "no rebind of the stuck function: " + string.Join("; ", actions));
            Harness.Assert(actions.Contains("pci-bind snd_hda_intel 0000:01:00.1"), "audio function restored: " + string.Join("; ", actions));
        });

        Harness.Scenario("Release_AfterWedge_RefusesFurtherUnbind", sb =>
        {
            DgpuFunction(sb, "0000:01:00.0", driver: true);
            var actions = new List<string>();
            GPUModeControl.DgpuUnbindWedged = true;
            GPUModeControl.PciActionForTest = (action, driver, bdf) => { actions.Add(action); return true; };
            GPUModeControl.UnbindHolderScanForTest = () => Array.Empty<NvidiaHolder>();
            Harness.Assert(!GPUModeControl.ReleaseNvidiaModulesAndPurgeHolders("0000:01:00.0", out _), "release refused");
            Harness.AssertEqual(0, actions.Count, "no PCI actions while wedged");
        });
    }
}
