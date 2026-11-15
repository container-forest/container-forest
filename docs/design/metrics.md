# Resource usage metrics

Decision: [ADR-0017](../adr/0017-metrics.md). Aggregator paths are relative to `src/Aggregator`, agent paths to `src/Agent`.

## Agent

- `Metrics/MetricsForwarder` runs beside heartbeats in each session and sends a `MetricsSample` every `Agent:MetricsInterval`; `MetricsCollector` takes one sample. A failed reading skips a sample; only a failed write ends the loop.
- **Source** (`MetricsCollector.DetectSourceAsync`, before `Hello`): cgroup v2 when Docker reports cgroup version 2 (or nothing) and `sys/fs/cgroup/cgroup.controllers` exists under the host root; otherwise Docker's stats API (`DockerDotNetGateway.ReadStatsAsync`, `one-shot`, four in parallel).
- **cgroup v2** (`CgroupV2Reader`): the directory is `system.slice/docker-<id>.scope` (systemd driver), `docker/<id>` (cgroupfs), or what `/proc/<pid>/cgroup` says. `cpu.stat`, `cpu.max`, `memory.current` − `inactive_file`, `memory.max`, `io.stat`, `pids.current`. Network comes from `/proc/<pid>/net/dev` of the main process; host-network containers report none. The pid is cached and checked against the cgroup before reuse.
- **Host** (`HostMetricsReader`): `/proc/stat`, `meminfo`, `loadavg`, `uptime`, `diskstats` (only devices with `/sys/block/<name>/device`), `/proc/1/net/dev` without `lo`, `veth*`, `docker0`, `br-*`, and disk file systems from `/proc/1/mounts`, one per device, sized with `DriveInfo` under the host root.
- **Rates** (`MetricsRates`): deltas over the time since the previous reading; a counter that went down or a host reboot gives `has_rates = false`.
- `docker system df` (`GetDiskUsageAsync`) runs in the background every `Agent:DiskUsageInterval`; its result rides with the next sample. Daemons before API 1.52 answer in a layout the client doesn't read, and send none.

## Aggregator

- `AgentGatewayService` maps a sample (`Api/Grpc/MetricsMapper`: lengths and counts capped, non-finite numbers zeroed, a clock more than a minute off replaced by the aggregator's) and hands it to `MetricsBuffer` and `ResourceAlertWatch`.
- `Application/Metrics/MetricsBuffer` (singleton): an hour of raw samples per environment, the latest `docker system df`, the agent's source, and a `MinuteAccumulator` per environment. A minute is finished by the first sample of the next one, or by the clock when the agent went silent.
- `Infrastructure/Metrics/MetricsWriter` (every minute) stores finished minutes through `IMetricsStore` (`SqliteMetricsStore`, plain SQL in `metrics.db`, tables `*_minute` and `*_hour` for containers, the host and file systems), puts them back after a failed write, rolls up ended hours, and once an hour deletes what is past retention and rows of deleted environments.
- `MetricsQueryService`: a range of an hour or less is raw samples (minutes before the oldest one in memory), longer ranges minutes or hours in about 360 buckets (`ChooseWindowAsync`). Stack graphs carry deployment markers (`ListByStackAsync`, last 200).
- **Settings:** the `Metrics` section (`MetricsSettingsSection`, `GET/PUT /api/settings/metrics`, administrators, audited): `MinuteRetentionDays`, `HourRetentionDays`, `AlertsEnabled`, `HostCpuPercent`, `HostMemoryPercent`, `DiskPercent`, `ContainerMemoryPercent` (0 turns one off), `AlertMinutes`.
- **Alerts** (`ResourceAlertWatch`, `ResourceAlertNotifier`): state in memory per environment and subject; `ResourceUsageNotificationDetails` is stored as the `resource-usage` details type.

## Clients

- REST: see the comment at the top of `Api/Contracts/MetricsContracts.cs`. Series are columns (`at[]` plus one array per measure).
- CLI: `forest container stats [--stack] [--watch]`, `forest env stats [--watch]` (`Commands/StatsCommands.cs`); the series endpoints are left out of the generated client.
- Bot: `/stats environment [stack]` (`StatsCommand`, `ReplyFormatter.Stats`), backed by `GetStats`.
- Panel: graphs are not there yet.
