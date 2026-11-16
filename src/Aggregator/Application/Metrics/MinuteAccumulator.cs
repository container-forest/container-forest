using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>
/// Averages one environment's samples of one minute. Rates count only samples that have them; gauges (memory, pids,
/// limits) count every sample. A container is counted by name: its id changes when compose recreates it.
/// </summary>
public sealed class MinuteAccumulator(EnvironmentId environmentId, long start)
{
    private readonly Dictionary<string, ContainerSums> containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Used, double Total)> filesystems = new(StringComparer.Ordinal);
    private HostSums? host;

    public long Start { get; } = start;

    public void Add(MetricsSnapshot snapshot)
    {
        if (snapshot.Host is { } h)
        {
            (host ??= new HostSums()).Add(h);
            foreach (var fs in h.Filesystems)
            {
                // The last reading of the minute: fullness does not need averaging.
                filesystems[fs.MountPoint] = (fs.UsedBytes, fs.UsedBytes + fs.AvailableBytes);
            }
        }

        foreach (var c in snapshot.Containers)
        {
            if (!containers.TryGetValue(c.Name, out var sums))
            {
                containers[c.Name] = sums = new ContainerSums();
            }

            sums.Add(c);
        }
    }

    public MinuteBatch Finish() => new(
        host?.ToPoint(environmentId, Start),
        containers.Select(c => c.Value.ToPoint(environmentId, c.Key, Start)).ToList(),
        filesystems.Select(f => new FilesystemPoint(environmentId, f.Key, Start, f.Value.Used, f.Value.Total)).ToList());

    private sealed class ContainerSums
    {
        private int samples;
        private int rated;
        private string stack = "", service = "";
        private double cpu, cpuMax, throttled, memory, memoryMax, cache, rx, tx, read, write, pidsMax, cpuLimit, memoryLimit;

        public void Add(ContainerSample c)
        {
            samples++;
            stack = c.StackName ?? "";
            service = c.ServiceName ?? "";
            memory += c.MemoryWorkingSetBytes;
            memoryMax = Math.Max(memoryMax, c.MemoryWorkingSetBytes);
            cache += c.MemoryCacheBytes;
            pidsMax = Math.Max(pidsMax, c.Pids);
            cpuLimit = c.CpuLimitCores;
            memoryLimit = c.MemoryLimitBytes;
            if (c.HasRates)
            {
                rated++;
                cpu += c.CpuCores;
                cpuMax = Math.Max(cpuMax, c.CpuCores);
                throttled += c.CpuThrottledRatio;
                rx += c.NetworkRxBytesPerSecond;
                tx += c.NetworkTxBytesPerSecond;
                read += c.BlockReadBytesPerSecond;
                write += c.BlockWriteBytesPerSecond;
            }
        }

        public ContainerPoint ToPoint(EnvironmentId environmentId, string name, long start)
        {
            double R(double sum) => rated > 0 ? sum / rated : 0;
            return new ContainerPoint(
                environmentId, name, start, stack, service,
                R(cpu), cpuMax, cpuLimit, R(throttled),
                memory / samples, memoryMax, memoryLimit, cache / samples,
                R(rx), R(tx), R(read), R(write), pidsMax);
        }
    }

    private sealed class HostSums
    {
        private int samples;
        private int rated;
        private double cpuCount, user, system, iowait, steal, busyMax, load1, memory, memoryMax, memoryTotal, swap, swapTotal;
        private double read, write, rx, tx;

        public void Add(HostSample h)
        {
            samples++;
            cpuCount = h.CpuCount;
            load1 += h.Load1;
            memory += h.MemoryUsedBytes;
            memoryMax = Math.Max(memoryMax, h.MemoryUsedBytes);
            memoryTotal = h.MemoryTotalBytes;
            swap += h.SwapUsedBytes;
            swapTotal = h.SwapTotalBytes;
            if (h.HasRates)
            {
                rated++;
                user += h.CpuUser;
                system += h.CpuSystem;
                iowait += h.CpuIowait;
                steal += h.CpuSteal;
                busyMax = Math.Max(busyMax, h.CpuBusy);
                read += h.DiskReadBytesPerSecond;
                write += h.DiskWriteBytesPerSecond;
                rx += h.NetworkRxBytesPerSecond;
                tx += h.NetworkTxBytesPerSecond;
            }
        }

        public HostPoint ToPoint(EnvironmentId environmentId, long start)
        {
            double R(double sum) => rated > 0 ? sum / rated : 0;
            return new HostPoint(
                environmentId, start, cpuCount, R(user), R(system), R(iowait), R(steal), busyMax, load1 / samples,
                memory / samples, memoryMax, memoryTotal, swap / samples, swapTotal, R(read), R(write), R(rx), R(tx));
        }
    }
}
