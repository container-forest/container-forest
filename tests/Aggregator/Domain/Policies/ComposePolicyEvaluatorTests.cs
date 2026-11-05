using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Policies;

namespace ContainerForest.Aggregator.Domain.Tests.Policies;

/// <summary>The rules of the compose policy (ADR-0012) on compose facts.</summary>
public class ComposePolicyEvaluatorTests
{
    private const string StackDirectory = "/opt/container-forest/stacks/web";

    private static readonly string[] AgentDirectories = ["/opt/container-forest", "/opt/container-forest/stacks", "/run/container-forest"];

    private readonly ComposePolicy policy = Policy();

    [Fact]
    public void Stack_and_data_directories_and_system_files_need_no_allowance()
    {
        var evaluation = Evaluate(Service(mounts:
        [
            Bind($"{StackDirectory}/nginx.conf", readOnly: true),
            Bind("/var/lib/container-forest/data/web", readOnly: false),
            Bind("/etc/localtime", readOnly: true),
            Bind("/usr/share/zoneinfo/Europe/Kyiv", readOnly: true),
            new MountFacts("volume", "db", false, null),
        ]));

        Assert.True(evaluation.Passes, string.Join("; ", evaluation.Violations));
    }

    [Theory]
    [InlineData("privileged")]
    [InlineData("host:network")]
    [InlineData("host:pid")]
    [InlineData("capability:SYS_ADMIN")]
    [InlineData("docker-socket")]
    [InlineData("port:443")]
    [InlineData("security-opt:seccomp:unconfined")]
    [InlineData("device:/dev/net/tun")]
    [InlineData("foreign-container")]
    [InlineData("label:com.container-forest.system")]
    [InlineData("container-name")]
    [InlineData("key:storage_opt_x")]
    public void Each_risky_setting_is_a_violation_with_the_allowance_that_lifts_it(string allowance)
    {
        var service = allowance switch
        {
            "privileged" => Service(privileged: true),
            "host:network" => Service(namespaces: new() { ["network_mode"] = "host" }),
            "host:pid" => Service(namespaces: new() { ["pid"] = "host" }),
            "capability:SYS_ADMIN" => Service(capAdd: ["cap_sys_admin"]),
            "docker-socket" => Service(mounts: [Bind("/var/run/docker.sock", readOnly: true)]),
            "port:443" => Service(ports: [443, 8080]),
            "security-opt:seccomp:unconfined" => Service(securityOpt: ["seccomp:unconfined", "no-new-privileges:true"]),
            "device:/dev/net/tun" => Service(devices: ["/dev/net/tun"]),
            "foreign-container" => Service(namespaces: new() { ["network_mode"] = "container:other-db" }),
            "label:com.container-forest.system" => Service(labels: ["com.container-forest.system", "traefik.enable"]),
            "container-name" => Service(containerName: "db"),
            _ => Service(unknownKeys: ["storage_opt_x"]),
        };

        var violation = Assert.Single(Evaluate(service).Violations);
        Assert.Equal(allowance, violation.Allowance?.Value);
        Assert.True(Evaluate(service, stackAllowances: [allowance]).Passes);
        Assert.True(Evaluate(service, Policy(allowances: [allowance])).Passes);
    }

    [Fact]
    public void Harmless_capabilities_and_unprivileged_ports_pass()
    {
        Assert.True(Evaluate(Service(capAdd: ["NET_ADMIN", "CHOWN"], ports: [8080, 5432])).Passes);
    }

    [Theory]
    [InlineData("/dev/sda")]
    [InlineData("/dev/nvme0n1")]
    [InlineData("/dev/mem")]
    public void Disks_and_memory_are_never_allowed(string device)
    {
        var service = Service(devices: [device]);

        var violation = Assert.Single(Evaluate(service, stackAllowances: [$"device:{device}"]).Violations);
        Assert.Null(violation.Allowance);
    }

    [Fact]
    public void Host_paths_need_an_allowance_that_covers_them_and_their_access()
    {
        var service = Service(mounts: [Bind("/srv/media/films", readOnly: false)]);

        Assert.Equal("path-rw:/srv/media/films", Assert.Single(Evaluate(service).Violations).Allowance?.Value);
        Assert.False(Evaluate(service, Policy(allowances: ["path:/srv/media"])).Passes);
        Assert.True(Evaluate(service, Policy(allowances: ["path-rw:/srv/media"])).Passes);
        Assert.False(Evaluate(service, Policy(allowances: ["path-rw:/srv/med"])).Passes);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/etc/ssh")]
    [InlineData("/var")]
    [InlineData("/var/lib/docker/volumes/other_db")]
    [InlineData("/opt/container-forest/stacks/other")]
    [InlineData("/run/container-forest/other")]
    [InlineData("/var/lib/container-forest/data/other")]
    public void Protected_paths_open_only_with_the_stacks_exact_allowance(string path)
    {
        var service = Service(mounts: [Bind(path, readOnly: true)]);

        Assert.False(Evaluate(service, Policy(allowances: [$"path:{path}", "path:/"])).Passes);
        // A prefix of the stack's own does not open it either, only the very path.
        Assert.Equal(path == "/", Evaluate(service, stackAllowances: ["path-rw:/"]).Passes);
        Assert.True(Evaluate(service, stackAllowances: [$"path:{path}"]).Passes);
    }

    [Fact]
    public void Writable_stack_directory_needs_an_exact_allowance_and_writable_subdirectories_warn()
    {
        var whole = Evaluate(Service(mounts: [Bind(StackDirectory, readOnly: false)]));
        var data = Evaluate(Service(mounts: [Bind($"{StackDirectory}/data", readOnly: false)]));

        Assert.Equal($"path-rw:{StackDirectory}", Assert.Single(whole.Violations).Allowance?.Value);
        Assert.True(data.Passes);
        Assert.Single(data.Warnings);
    }

    [Fact]
    public void A_mount_inside_another_writable_mount_is_refused_outright()
    {
        var facts = Facts(
            Service("writer", mounts: [Bind($"{StackDirectory}/data", readOnly: false)]),
            Service("reader", mounts: [Bind($"{StackDirectory}/data/link", readOnly: true)]));

        var violation = Assert.Single(ComposePolicyEvaluator.Evaluate(facts, policy, "web", []).Violations);
        Assert.Null(violation.Allowance);
    }

    [Fact]
    public void Links_and_paths_the_agent_cannot_see_are_refused_except_system_files()
    {
        var facts = Facts(Service()) with
        {
            InspectedMounts =
            [
                new InspectedMount("/var/lib/container-forest/data/web/x", "/var/lib/container-forest/data/web/x"),
                new InspectedMount("/srv/media", null),
                new InspectedMount("/etc/localtime", "/etc/localtime"),
            ],
        };

        var violations = ComposePolicyEvaluator.Evaluate(facts, policy, "web", ["path:/srv/media"]).Violations;

        Assert.Equal(2, violations.Count);
        Assert.All(violations, v => Assert.Null(v.Allowance));
    }

    [Fact]
    public void Links_only_root_on_the_host_could_make_are_not_traps()
    {
        var facts = Facts(Service(mounts: [Bind("/var/run/docker.sock", readOnly: false)])) with
        {
            InspectedMounts = [new InspectedMount("/var/run/docker.sock", "/var/run")],
        };

        var violation = Assert.Single(ComposePolicyEvaluator.Evaluate(facts, policy, "web", []).Violations);
        Assert.Equal("docker-socket", violation.Allowance?.Value);
        Assert.True(ComposePolicyEvaluator.Evaluate(facts, policy, "web", ["docker-socket"]).Passes);
    }

    [Fact]
    public void Links_where_a_writable_allowance_reaches_are_traps()
    {
        var facts = Facts(Service(mounts: [Bind("/srv/app/data/x", readOnly: true)])) with
        {
            InspectedMounts = [new InspectedMount("/srv/app/data/x", "/srv/app/data/x")],
        };

        Assert.True(ComposePolicyEvaluator.Evaluate(facts, policy, "web", ["path:/srv/app"]).Passes);
        var violation = Assert.Single(ComposePolicyEvaluator.Evaluate(facts, policy, "web", ["path-rw:/srv/app"]).Violations);
        Assert.Null(violation.Allowance);
    }

    [Theory]
    [InlineData("web", true)]
    [InlineData("web-db", true)]
    [InlineData("webdb", false)]
    [InlineData("db", false)]
    public void Container_may_be_named_after_its_stack(string containerName, bool passes) =>
        Assert.Equal(passes, Evaluate(Service(containerName: containerName)).Passes);

    [Theory]
    [InlineData("web-db", "web-db")]
    [InlineData("web-db-1", "web-db")]
    [InlineData("web-api", "web")]
    [InlineData("web", "web")]
    [InlineData("redis", null)]
    public void Container_names_belong_to_the_stack_with_the_longest_matching_name(string containerName, string? owner) =>
        Assert.Equal(owner, ComposePolicyEvaluator.ContainerNameOwner(containerName, ["web", "web-db", "db"]));

    [Fact]
    public void Stack_cannot_name_a_container_after_another_stack()
    {
        var facts = Facts(Service(containerName: "web-db-cache"));

        Assert.False(ComposePolicyEvaluator.Evaluate(facts, policy, "web", [], ["web", "web-db"]).Passes);
        Assert.True(ComposePolicyEvaluator.Evaluate(facts, policy, "web", [], ["web"]).Passes);
    }

    [Fact]
    public void Volumes_and_networks_outside_the_stack_need_allowances()
    {
        var facts = Facts(Service()) with
        {
            Volumes =
            [
                new NamedResource("data", "web_data", false, null, false),
                new NamedResource("other", "other_db", false, null, false),
                new NamedResource("raw", "web_raw", false, "local", HasDriverOptions: true),
            ],
            Networks =
            [
                new NamedResource("default", "web_default", false, null, false),
                new NamedResource("proxy", "proxy", External: true, null, false),
                new NamedResource("lan", "web_lan", false, "macvlan", false),
            ],
        };

        var allowances = ComposePolicyEvaluator.Evaluate(facts, policy, "web", []).Violations.Select(v => v.Allowance?.Value).ToList();

        Assert.Equal(["volume:other_db", "volume-driver", "network:proxy", "network-driver:macvlan"], allowances);
    }

    [Fact]
    public void Secret_files_outside_the_stack_directory_are_host_paths_too()
    {
        var facts = Facts(Service()) with { Files = [$"{StackDirectory}/secrets/pw", "/etc/shadow"] };

        var violation = Assert.Single(ComposePolicyEvaluator.Evaluate(facts, policy, "web", []).Violations);
        Assert.Contains("/etc/shadow", violation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("privileged:yes")]
    [InlineData("capability:")]
    [InlineData("path:relative")]
    [InlineData("path:/a/../b")]
    [InlineData("port:8080")]
    [InlineData("host:mount")]
    [InlineData("whatever")]
    public void Invalid_allowances_are_rejected(string allowance) =>
        Assert.Throws<DomainValidationException>(() => PolicyAllowance.Parse(allowance));

    [Fact]
    public void Allowances_are_normalized()
    {
        Assert.Equal("capability:SYS_ADMIN", PolicyAllowance.Parse(" capability:cap_sys_admin ").Value);
        Assert.Equal("path:/srv/media", PolicyAllowance.Parse("path:/srv/media/").Value);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/etc/forest")]
    [InlineData("/opt/container-forest/data")]
    [InlineData("/")]
    public void Data_root_must_be_an_absolute_path_outside_protected_directories(string root) =>
        Assert.Throws<DomainValidationException>(() => Policy(dataRoot: root));

    private ComposeFacts Facts(params ServiceFacts[] services) =>
        new(StackDirectory, AgentDirectories, services, [], [], [], []);

    private PolicyEvaluation Evaluate(ServiceFacts service, ComposePolicy? with = null, string[]? stackAllowances = null) =>
        ComposePolicyEvaluator.Evaluate(Facts(service), with ?? policy, "web", stackAllowances ?? []);

    private static ComposePolicy Policy(string dataRoot = ComposePolicy.DefaultDataRoot, string[]? allowances = null)
    {
        var result = ComposePolicy.Default(EnvironmentId.New());
        result.Change(ComposePolicyMode.Enforce, dataRoot, ComposePolicy.DefaultSystemReadOnlyPaths, allowances ?? []);
        return result;
    }

    private static MountFacts Bind(string source, bool readOnly) => new("bind", source, readOnly, null);

    private static ServiceFacts Service(
        string name = "app",
        bool privileged = false,
        Dictionary<string, string>? namespaces = null,
        string[]? capAdd = null,
        string[]? securityOpt = null,
        string[]? devices = null,
        MountFacts[]? mounts = null,
        int[]? ports = null,
        string[]? labels = null,
        string? containerName = null,
        string[]? unknownKeys = null) =>
        new(
            name,
            privileged,
            namespaces ?? [],
            capAdd ?? [],
            securityOpt ?? [],
            devices ?? [],
            Gpus: false,
            DeviceCgroupRules: false,
            CgroupParent: false,
            Runtime: null,
            Provider: false,
            UseApiSocket: false,
            mounts ?? [],
            ports ?? [],
            [],
            labels ?? [],
            containerName,
            unknownKeys ?? []);
}
