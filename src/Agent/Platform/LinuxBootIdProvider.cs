namespace ContainerForest.Agent.Platform;

/// <summary>
/// Reads the kernel's boot id. A container shares the kernel with the host, so the agent in a container
/// gets the host's boot id.
/// </summary>
public sealed class LinuxBootIdProvider(string path = LinuxBootIdProvider.DefaultPath) : IBootIdProvider
{
    public const string DefaultPath = "/proc/sys/kernel/random/boot_id";

    public string GetBootId()
    {
        var bootId = File.ReadAllText(path).Trim();
        if (bootId.Length == 0)
        {
            throw new InvalidOperationException($"{path} is empty.");
        }

        return bootId;
    }
}
