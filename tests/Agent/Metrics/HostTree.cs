namespace ContainerForest.Agent.Tests.Metrics;

/// <summary>A temporary directory standing in for the host root mounted into the agent (/host).</summary>
internal sealed class HostTree : IDisposable
{
    public HostTree()
    {
        Root = Directory.CreateTempSubdirectory("cf-host-").FullName;
    }

    public string Root { get; }

    public HostTree File(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return this;
    }

    public HostTree Directory_(string relative)
    {
        Directory.CreateDirectory(Path.Combine(Root, relative));
        return this;
    }

    /// <summary>A container's cgroup directory with the files the reader looks at.</summary>
    public HostTree Cgroup(string directory, ulong usageUsec, ulong memory = 1000, string cpuMax = "max 100000", string memoryMax = "max")
    {
        var dir = $"sys/fs/cgroup/{directory}";
        return File($"{dir}/cgroup.procs", "1\n")
            .File($"{dir}/cpu.stat", $"usage_usec {usageUsec}\nuser_usec 1\nsystem_usec 1\nnr_periods 0\nnr_throttled 0\nthrottled_usec 0\n")
            .File($"{dir}/cpu.max", cpuMax + "\n")
            .File($"{dir}/memory.current", $"{memory}\n")
            .File($"{dir}/memory.stat", "anon 600\nfile 400\ninactive_file 100\n")
            .File($"{dir}/memory.max", memoryMax + "\n")
            .File($"{dir}/io.stat", "8:0 rbytes=10 wbytes=20 rios=1 wios=1\n")
            .File($"{dir}/pids.current", "7\n");
    }

    public HostTree NetDev(long pid, ulong rx, ulong tx) =>
        File($"proc/{pid}/net/dev", $"h1\nh2\n    lo: 9 0 0 0 0 0 0 0 9 0 0 0 0 0 0 0\n  eth0: {rx} 0 0 0 0 0 0 0 {tx} 0 0 0 0 0 0 0\n");

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
