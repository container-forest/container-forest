namespace ContainerForest.Agent.Platform;

/// <summary>
/// Fallback for systems without a kernel boot id (Windows, macOS): the boot time, computed from the uptime.
/// It is rounded to a minute so that drift between the wall clock and the tick counter does not look like a reboot.
/// </summary>
public sealed class UptimeBootIdProvider(TimeProvider time, Func<long>? uptimeMilliseconds = null) : IBootIdProvider
{
    private readonly Func<long> uptime = uptimeMilliseconds ?? (() => Environment.TickCount64);

    public string GetBootId()
    {
        var bootTime = time.GetUtcNow() - TimeSpan.FromMilliseconds(uptime());
        var minutes = (long)Math.Round(bootTime.ToUnixTimeMilliseconds() / 60_000.0, MidpointRounding.AwayFromZero);
        return $"boot-{minutes * 60}";
    }
}
