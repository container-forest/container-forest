using System.Globalization;

namespace ContainerForest.Aggregator.Domain.Policies;

/// <param name="Service">The service, or null for something of the stack as a whole (a volume, a network).</param>
/// <param name="Allowance">The allowance that lifts it; null when nothing does, only an exemption (ADR-0012).</param>
/// <param name="StackOnly">Only the stack's own allowance lifts it, not the environment's (a protected path).</param>
public sealed record PolicyViolation(string? Service, string Message, PolicyAllowance? Allowance, bool StackOnly = false)
{
    public override string ToString()
    {
        var where = Service is null ? "" : $"service '{Service}': ";
        var how = Allowance is null ? "" : $" (allowance '{Allowance}')";
        return where + Message + how;
    }
}

/// <summary>The outcome of checking a stack: what breaks the policy and what is only worth a warning.</summary>
public sealed record PolicyEvaluation(IReadOnlyList<PolicyViolation> Violations, IReadOnlyList<string> Warnings)
{
    public bool Passes => Violations.Count == 0;
}

/// <summary>
/// Checks a stack's compose facts against its environment's policy and the stack's own allowances (ADR-0012). The rules
/// keep a stack from reaching the host and other stacks: host namespaces, privileges, devices, the Docker socket, host
/// paths outside the stack's own directories, other stacks' volumes and networks, reserved labels and names.
/// </summary>
public static class ComposePolicyEvaluator
{
    /// <summary>Docker's default capabilities and a few that act only within the container's own namespaces.</summary>
    public static readonly IReadOnlySet<string> HarmlessCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "SETGID", "SETUID", "SETPCAP", "NET_BIND_SERVICE",
        "NET_RAW", "SYS_CHROOT", "MKNOD", "AUDIT_WRITE", "SETFCAP", "NET_ADMIN", "IPC_LOCK", "SYS_NICE", "SYS_RESOURCE",
    };

    /// <summary>Keys of a service in compose's model; anything else is unknown and needs an allowance.</summary>
    public static readonly IReadOnlySet<string> KnownServiceKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "annotations", "attach", "blkio_config", "cap_add", "cap_drop", "cgroup", "cgroup_parent", "command", "configs",
        "container_name", "cpu_count", "cpu_percent", "cpu_period", "cpu_quota", "cpu_rt_period", "cpu_rt_runtime",
        "cpu_shares", "cpus", "cpuset", "depends_on", "deploy", "develop", "device_cgroup_rules", "devices", "dns",
        "dns_opt", "dns_search", "domainname", "entrypoint", "environment", "expose", "external_links", "extra_hosts",
        "gpus", "group_add", "healthcheck", "hostname", "image", "init", "ipc", "isolation", "labels", "label_file",
        "links", "logging", "mac_address", "mem_limit", "mem_reservation", "mem_swappiness", "memswap_limit", "models",
        "network_mode", "networks", "oom_kill_disable", "oom_score_adj", "pid", "pids_limit", "platform", "ports",
        "post_start", "pre_stop", "privileged", "profiles", "provider", "pull_policy", "pull_refresh_after", "read_only",
        "restart", "runtime", "scale", "secrets", "security_opt", "shm_size", "stdin_open", "stop_grace_period",
        "stop_signal", "storage_opt", "sysctls", "tmpfs", "tty", "ulimits", "use_api_socket", "user", "userns_mode",
        "uts", "volumes", "volumes_from", "working_dir",
    };

    private static readonly string[] ReservedLabelPrefixes = ["com.container-forest.", "com.docker.compose."];

    // Raw disks and memory: whoever has them owns the host, whatever the allowlist says.
    private static readonly string[] NeverAllowedDevicePrefixes =
    [
        "/dev/sd", "/dev/hd", "/dev/vd", "/dev/xvd", "/dev/nvme", "/dev/mmcblk", "/dev/dm-", "/dev/mapper/", "/dev/loop",
        "/dev/md", "/dev/nbd", "/dev/zram", "/dev/disk/", "/dev/mem", "/dev/kmem", "/dev/port",
    ];

    private static readonly string[] SocketNames = ["docker.sock", "containerd.sock", "podman.sock"];

    /// <param name="environmentStacks">Names of the environment's stacks, this one included: container names belong to them.</param>
    public static PolicyEvaluation Evaluate(
        ComposeFacts facts,
        ComposePolicy policy,
        string stackName,
        IEnumerable<string> stackAllowances,
        IReadOnlyCollection<string>? environmentStacks = null)
    {
        var context = new Context(facts, policy, stackName, stackAllowances.Select(PolicyAllowance.Parse).ToList())
        {
            EnvironmentStacks = environmentStacks ?? [stackName],
        };
        foreach (var service in facts.Services)
        {
            CheckService(context, service);
        }

        CheckMounts(context);
        CheckResources(context, facts.Volumes, PolicyAllowance.Volume, "volume", d => d is null or "local", PolicyAllowance.VolumeDriver);
        CheckResources(context, facts.Networks, PolicyAllowance.Network, "network", d => d is null or "bridge", null);
        return new PolicyEvaluation(context.Violations, context.Warnings);
    }

    private static void CheckService(Context context, ServiceFacts service)
    {
        var name = service.Name;
        if (service.Privileged)
        {
            context.Require(name, "runs privileged", PolicyAllowance.Flag(PolicyAllowance.Privileged));
        }

        foreach (var (key, value) in service.Namespaces)
        {
            var ns = key switch { "network_mode" => "network", "userns_mode" => "userns", _ => key };
            if (value == "host" && PolicyAllowance.HostNamespaces.Contains(ns))
            {
                context.Require(name, $"shares the host's {ns} namespace ({key}: host)", PolicyAllowance.Of(PolicyAllowance.Host, ns));
            }
            else if (value.StartsWith("container:", StringComparison.Ordinal))
            {
                context.Require(name, $"joins another container ({key}: {value})", PolicyAllowance.Flag(PolicyAllowance.ForeignContainer));
            }
        }

        foreach (var capability in service.CapAdd)
        {
            var capName = PolicyAllowance.CapabilityName(capability) ?? capability;
            if (!HarmlessCapabilities.Contains(capName))
            {
                context.Require(name, $"adds capability {capName}", PolicyAllowance.Of(PolicyAllowance.Capability, capName));
            }
        }

        foreach (var option in service.SecurityOpt)
        {
            if (option.Replace('=', ':') is not ("no-new-privileges" or "no-new-privileges:true"))
            {
                context.Require(name, $"sets security_opt '{option}'", PolicyAllowance.Of(PolicyAllowance.SecurityOpt, option));
            }
        }

        foreach (var device in service.Devices)
        {
            if (NeverAllowedDevicePrefixes.Any(p => device.StartsWith(p, StringComparison.Ordinal)))
            {
                context.Forbid(name, $"uses device '{device}', a disk or the host's memory");
            }
            else if (HostPath.TryNormalize(device) is { } path)
            {
                context.Require(name, $"uses device '{device}'", PolicyAllowance.Of(PolicyAllowance.Device, path));
            }
            else
            {
                context.Forbid(name, $"uses device '{device}', which is not an absolute path");
            }
        }

        Flag(context, name, service.Gpus, "uses GPUs", PolicyAllowance.Gpus);
        Flag(context, name, service.DeviceCgroupRules, "sets device_cgroup_rules", PolicyAllowance.DeviceCgroupRules);
        Flag(context, name, service.CgroupParent, "sets cgroup_parent", PolicyAllowance.CgroupParent);
        Flag(context, name, service.Provider, "runs a provider on the agent", PolicyAllowance.Provider);
        Flag(context, name, service.UseApiSocket, "gets the Docker API socket (use_api_socket)", PolicyAllowance.DockerSocket);
        if (service.Runtime is { } runtime && runtime != "runc")
        {
            context.Require(name, $"uses the runtime '{runtime}'", PolicyAllowance.Of(PolicyAllowance.Runtime, runtime));
        }

        foreach (var port in service.PublishedPorts.Where(p => p is > 0 and < 1024).Distinct())
        {
            context.Require(name, $"publishes host port {port}", PolicyAllowance.Of(PolicyAllowance.Port, port.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var source in service.VolumesFrom.Where(v => v.StartsWith("container:", StringComparison.Ordinal)))
        {
            context.Require(name, $"takes the volumes of another container ({source})", PolicyAllowance.Flag(PolicyAllowance.ForeignContainer));
        }

        foreach (var label in service.LabelKeys.Where(l => ReservedLabelPrefixes.Any(p => l.StartsWith(p, StringComparison.Ordinal))))
        {
            context.Require(name, $"sets the reserved label '{label}'", PolicyAllowance.Of(PolicyAllowance.Label, label));
        }

        if (service.ContainerName is { } containerName && ContainerNameOwner(containerName, context.EnvironmentStacks) != context.StackName)
        {
            context.Require(
                name,
                $"is named '{containerName}', which does not belong to the stack: names belong to the stack with the longest name " +
                $"they equal or start with followed by '-'",
                PolicyAllowance.Flag(PolicyAllowance.ContainerName));
        }

        foreach (var key in service.UnknownKeys)
        {
            context.Require(name, $"uses '{key}', which the policy does not know", PolicyAllowance.Of(PolicyAllowance.Key, key));
        }
    }

    private static void CheckMounts(Context context)
    {
        var binds = context.Facts.Services
            .SelectMany(s => s.Mounts.Where(m => m.Type == "bind" && m.Source is not null).Select(m => (Service: s.Name, Mount: m)))
            .ToList();
        var writable = binds.Where(b => !b.Mount.ReadOnly).Select(b => HostPath.TryNormalize(b.Mount.Source!)).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (var (service, mount) in binds)
        {
            if (mount.Propagation is { Length: > 0 } propagation && propagation is not ("rprivate" or "private"))
            {
                context.Require(service, $"mounts '{mount.Source}' with {propagation} propagation", PolicyAllowance.Flag(PolicyAllowance.Propagation));
            }

            CheckHostPath(context, service, mount.Source!, mount.ReadOnly, writable);
        }

        foreach (var file in context.Facts.Files)
        {
            CheckHostPath(context, null, file, readOnly: true, writable);
        }

        var stackAllowances = context.StackAllowances;
        foreach (var inspected in context.Facts.InspectedMounts)
        {
            if (context.IsSystemPath(inspected.Path))
            {
                continue;
            }

            if (inspected.Link is { } link)
            {
                // A link only root on the host could have made, such as /var/run → /run, is part of the host, not a trap.
                if (!IsContainerWritable(link, context.Facts, context.Policy, context.StackName, stackAllowances))
                {
                    continue;
                }

                context.Forbid(null, $"mount source '{inspected.Path}' goes through the symbolic link '{link}'");
            }
            else
            {
                context.Forbid(null, $"the agent cannot check mount source '{inspected.Path}'; mount the host's root into the agent (CF_AGENT__HOSTROOT)");
            }
        }
    }

    /// <summary>
    /// The stack a container name belongs to: the one of <paramref name="stackNames"/> with the longest name the
    /// container name equals or starts with followed by '-'. Stack names contain '-' themselves, so with stacks "web"
    /// and "web-db" the name "web-db-1" belongs to "web-db". A stack added later cannot take a name an existing stack's
    /// container has (<c>StackService.CreateAsync</c>). Null when no stack's name fits.
    /// </summary>
    public static string? ContainerNameOwner(string containerName, IEnumerable<string> stackNames) =>
        stackNames
            .Where(s => containerName == s || containerName.StartsWith(s + "-", StringComparison.Ordinal))
            .OrderByDescending(s => s.Length)
            .FirstOrDefault();

    /// <summary>
    /// Whether a container may write at <paramref name="path"/>, so it could have put a symbolic link there: the stack's
    /// directory, the data root, a writable bind mount of this stack, or a writable path an allowance opens. Anything
    /// else only root on the host changes. Writable paths other stacks got through their own allowances are not known
    /// here; keep such allowances for the environment, where every stack sees them.
    /// </summary>
    public static bool IsContainerWritable(
        string path, ComposeFacts facts, ComposePolicy policy, string stackName, IEnumerable<string> stackAllowances)
    {
        var roots = new List<string> { facts.StackDirectory, policy.DataRoot };
        roots.AddRange(facts.Services
            .SelectMany(s => s.Mounts)
            .Where(m => m.Type == "bind" && !m.ReadOnly && m.Source is not null)
            .Select(m => HostPath.TryNormalize(m.Source!))
            .OfType<string>());
        roots.AddRange(policy.ParsedAllowances.Concat(stackAllowances.Select(PolicyAllowance.Parse))
            .Where(a => a.Kind == PolicyAllowance.PathReadWrite)
            .Select(a => a.Argument!));
        return roots.Any(root => root != "/" && HostPath.IsAtOrUnder(path, root));
    }

    private static void CheckHostPath(Context context, string? service, string source, bool readOnly, IReadOnlySet<string> writable)
    {
        if (HostPath.TryNormalize(source) is not { } path)
        {
            context.Forbid(service, $"mounts '{source}', which is not an absolute normalized host path");
            return;
        }

        var access = readOnly ? PolicyAllowance.Path : PolicyAllowance.PathReadWrite;
        var exact = PolicyAllowance.Of(access, path);
        if (SocketNames.Contains(System.IO.Path.GetFileName(path)))
        {
            context.Require(service, $"mounts the container runtime's socket '{path}'", PolicyAllowance.Flag(PolicyAllowance.DockerSocket));
            return;
        }

        if (writable.FirstOrDefault(w => w != path && HostPath.IsAtOrUnder(path, w)) is { } outer)
        {
            context.Forbid(service, $"mounts '{path}', which lies inside '{outer}', mounted writable: a container could put a link there");
        }

        var stack = context.Facts.StackDirectory;
        if (HostPath.IsAtOrUnder(path, stack))
        {
            if (path == stack && !readOnly)
            {
                context.RequireExact(service, $"mounts the whole stack directory writable", exact);
            }
            else if (!readOnly)
            {
                context.Warn(service, $"mounts '{path}' in the stack directory writable; keep data in '{context.Policy.DataDirectoryOf(context.StackName)}'");
            }

            return;
        }

        if (HostPath.IsAtOrUnder(path, context.Policy.DataDirectoryOf(context.StackName)))
        {
            return;
        }

        if (readOnly && context.IsSystemPath(path))
        {
            return;
        }

        if (context.ProtectedPaths.FirstOrDefault(p => p == "/" ? path == "/" : HostPath.Overlaps(path, p)) is { } protectedPath)
        {
            context.RequireExact(service, $"mounts '{path}', which reaches the protected '{protectedPath}'", exact);
            return;
        }

        context.Require(service, $"mounts host path '{path}'{(readOnly ? " read-only" : " writable")}", exact);
    }

    private static void CheckResources(
        Context context, IReadOnlyList<NamedResource> resources, string kind, string what, Func<string?, bool> plainDriver, string? driverFlag)
    {
        foreach (var resource in resources)
        {
            if (resource.External || !resource.Name.StartsWith(context.StackName + "_", StringComparison.Ordinal))
            {
                context.Require(null, $"uses the {what} '{resource.Name}' outside the stack", PolicyAllowance.Of(kind, resource.Name));
            }

            if (!plainDriver(resource.Driver) || resource.HasDriverOptions)
            {
                var description = $"{what} '{resource.Key}' uses driver '{resource.Driver ?? "default"}'" + (resource.HasDriverOptions ? " with options" : "");
                context.Require(
                    null,
                    description,
                    driverFlag is not null ? PolicyAllowance.Flag(driverFlag) : PolicyAllowance.Of(PolicyAllowance.NetworkDriver, resource.Driver ?? "bridge"));
            }
        }
    }

    private static void Flag(Context context, string service, bool present, string message, string flag)
    {
        if (present)
        {
            context.Require(service, message, PolicyAllowance.Flag(flag));
        }
    }

    private sealed class Context(ComposeFacts facts, ComposePolicy policy, string stackName, IReadOnlyList<PolicyAllowance> stackAllowances)
    {
        public IEnumerable<string> StackAllowances => stackAllowances.Select(a => a.Value);

        public required IReadOnlyCollection<string> EnvironmentStacks { get; init; }

        private readonly IReadOnlyList<PolicyAllowance> environmentAllowances = policy.ParsedAllowances.ToList();

        public ComposeFacts Facts { get; } = facts;

        public ComposePolicy Policy { get; } = policy;

        public string StackName { get; } = stackName;

        public List<PolicyViolation> Violations { get; } = [];

        public List<string> Warnings { get; } = [];

        /// <summary>The fixed protected paths, the data root (other stacks' data) and the agent's own directories.</summary>
        public IReadOnlyList<string> ProtectedPaths { get; } =
        [
            .. ComposePolicy.ProtectedPaths,
            policy.DataRoot,
            .. facts.AgentDirectories.Select(HostPath.TryNormalize).OfType<string>(),
        ];

        public bool IsSystemPath(string path) => Policy.SystemReadOnlyPaths.Any(p => HostPath.IsAtOrUnder(path, p));

        public void Require(string? service, string message, PolicyAllowance allowance)
        {
            if (!environmentAllowances.Concat(stackAllowances).Any(a => a.Covers(allowance)))
            {
                Violations.Add(new PolicyViolation(service, message, allowance));
            }
        }

        /// <summary>Only the stack's own allowance with this very path lifts it.</summary>
        public void RequireExact(string? service, string message, PolicyAllowance allowance)
        {
            if (!stackAllowances.Any(a => a.CoversExactly(allowance)))
            {
                Violations.Add(new PolicyViolation(service, message + "; only an allowance of this stack with the exact path opens it", allowance, StackOnly: true));
            }
        }

        public void Forbid(string? service, string message) => Violations.Add(new PolicyViolation(service, message, null));

        public void Warn(string? service, string message) => Warnings.Add(service is null ? message : $"service '{service}': {message}");
    }
}
