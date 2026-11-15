# ADR-0017. Resource usage metrics: collected by the agent, stored by the aggregator in a database of its own

- Status: accepted
- Date: 2026-11-15

## Context

The panel showed container state and logs, but not how much CPU, memory, network and disk a container, a stack or a host uses. Answering "why is the host slow" or "did the last deployment start leaking memory" needed SSH and `docker stats`.

Options considered:

1. **cAdvisor / node-exporter + Prometheus or VictoriaMetrics + Grafana.** The most capable, but three or four more services per host, a separate sign-in that ignores the system's roles, and nothing in the panel, CLI or bot. Rejected for now; a Prometheus endpoint on the aggregator may come later.
2. **Docker's stats API polled by the agent.** No extra mounts, but one call per container, each up to a second or two without `one-shot`: on a host with dozens of containers that is constant load on the daemon. Kept only as the fallback.
3. **The agent reads the host's cgroup v2 and /proc files itself** (chosen), through the host root it already mounts read-only (ADR-0012).

## Decision

1. **Collection.** Every `Agent:MetricsInterval` (10 s; 0 turns it off) the agent sends a `MetricsSample`: per running container CPU, throttling, memory working set and cache against the limit, network (through the netns of its main process), block I/O and pids; for the host CPU by kind (including steal), load, memory, swap, disks by mount point, physical disk I/O and interfaces. The agent turns counters into rates itself; a counter that went down (a restart) gives a sample without rates. Docker's stats API is used on cgroup v1 or when a container's cgroup isn't visible. `docker system df` runs beside the samples every `Agent:DiskUsageInterval` (15 min). `Hello` says which source the agent uses.
2. **Storage.** The aggregator keeps the last hour of raw samples in memory and writes per-minute averages and maxima to `metrics.db` (`ConnectionStrings:Metrics`, next to `aggregator.db` by default), rolled up into hours. Containers are keyed by name, so a redeploy doesn't break a graph. Retention (7 days of minutes, 90 of hours by default) is the `Metrics` settings section (`CF_METRICS__*` or the panel). `metrics.db` is **not** in the backup: it is history, and losing it loses nothing else.
3. **Visibility** is that of containers: a container of a managed stack needs a role on the stack or its environment; the host, unmanaged stacks and containers outside compose need a role on the whole environment (a public environment gives every user one as a Viewer).
4. **Alerts.** Host CPU, host memory, every disk and the memory of containers with a limit are checked against thresholds of the same section; a value above its threshold for `AlertMinutes` sends `ResourceUsageHigh`, as long below it `ResourceUsageNormal`, through the ordinary notification rules.
5. **Clients.** REST (`/api/.../metrics`), `forest container stats` / `forest env stats`, `/stats` in the bot (`GetStats` in `bot.proto`).

## Consequences

- The aggregator gains one write per environment and minute; about 1.5 MB of `metrics.db` per container with the default retention.
- No GPU or temperature numbers; the protocol can add them as new fields.
- Raw samples are lost on an aggregator restart; graphs of the last hour then show minutes.
