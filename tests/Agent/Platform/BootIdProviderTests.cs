using ContainerForest.Agent.Platform;

namespace ContainerForest.Agent.Tests.Platform;

public class BootIdProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Boot_id_stays_the_same_while_the_system_runs()
    {
        var first = new UptimeBootIdProvider(new FixedTimeProvider(Now), () => 3_600_000).GetBootId();
        // Ten minutes later, with 2 s of drift between the wall clock and the tick counter.
        var later = new UptimeBootIdProvider(new FixedTimeProvider(Now.AddMinutes(10)), () => 4_202_000).GetBootId();

        Assert.Equal(first, later);
    }

    [Fact]
    public void Boot_id_changes_after_a_reboot()
    {
        var before = new UptimeBootIdProvider(new FixedTimeProvider(Now), () => 3_600_000).GetBootId();
        var after = new UptimeBootIdProvider(new FixedTimeProvider(Now.AddMinutes(10)), () => 60_000).GetBootId();

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Linux_boot_id_is_read_and_trimmed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "0b6e1c9e-8a55-4c8f-9d1e-3f2a7c4b5d6e\n");

            Assert.Equal("0b6e1c9e-8a55-4c8f-9d1e-3f2a7c4b5d6e", new LinuxBootIdProvider(path).GetBootId());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Real_linux_boot_id_is_available()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux has a kernel boot id.");

        Assert.Matches("^[0-9a-f-]{36}$", new LinuxBootIdProvider().GetBootId());
    }
}
