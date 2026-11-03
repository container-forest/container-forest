using System.Text;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class VerifyCommitStepTests
{
    /// <summary>Made with ssh-keygen; the verifier is a fake, so only the key it names matters here.</summary>
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINX3MltKY5wuuud0d6OKbOIYmZfhVtfR7HGKjMX7b8BU alice@laptop";

    private const string Signature = """
        -----BEGIN SSH SIGNATURE-----
        U1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAg1fcyW0pjnC6653R3o4ps4hiZl+
        FW19HscYqMxftvwFQAAAADZ2l0AAAAAAAAAAZzaGE1MTIAAABTAAAAC3NzaC1lZDI1NTE5
        AAAAQNvnQrdkjPedsaxUazhjASDKX1Ih3IncXTdE8UZFBxpbesT6oDLtak9yYr4nUZLBit
        kP+Fw7yxLvHFa7iN5miAU=
        -----END SSH SIGNATURE-----
        """;

    private static readonly CommitSha Deployed = CommitSha.Parse("1111111111111111111111111111111111111111");

    private readonly FakeCommitInspector inspector = new();
    private readonly FakeSignatureVerifier verifier = new();
    private readonly InMemorySigningKeys keys = new();
    private readonly InMemoryUsers users = new();
    private readonly InMemoryRoleAssignments roles = new();
    private readonly User alice = User.Create("alice", Now);

    public VerifyCommitStepTests() => users.Add(alice);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stack_that_does_not_require_signatures_is_not_checked()
    {
        var context = Running();
        context.Stack.SetRequireSignedCommits(false);

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(CommitEndorsement.NotChecked, context.Deployment.Endorsement);
        Assert.Equal(0, inspector.SignatureReads);
    }

    [Fact]
    public async Task Commit_signed_by_an_editor_of_the_stack_is_endorsed()
    {
        var context = Running();
        var key = AddKey(context, Role.Editor);

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(CommitEndorsement.Signed, context.Deployment.Endorsement);
        Assert.Equal(alice.Id, context.Deployment.SignedBy);
        Assert.Equal("alice", context.Deployment.SignerName);
        Assert.Equal(key.Fingerprint, context.Deployment.SigningKeyFingerprint);
        Assert.Equal(Now, key.LastUsedAt);
        Assert.Contains(Lines(context, DeploymentLogLevel.Info), l => l.Contains("signed with alice's key 'laptop'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsigned_commit_is_rejected_before_anything_is_decrypted()
    {
        var context = Running();
        inspector.Signature = null;

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Contains("is not signed", error.Message, StringComparison.Ordinal);
        Assert.Contains("approve commit", error.Message, StringComparison.Ordinal);
        Assert.Equal(CommitEndorsement.Rejected, context.Deployment.Endorsement);
        Assert.Equal(EndorsementProblem.Unsigned, context.Deployment.EndorsementProblem);
    }

    [Theory]
    [InlineData(Role.Operator)]
    [InlineData(Role.Viewer)]
    public async Task Signer_who_may_not_deploy_does_not_endorse(Role role)
    {
        var context = Running();
        AddKey(context, role);

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Equal(EndorsementProblem.SignerMayNotDeploy, context.Deployment.EndorsementProblem);
    }

    [Fact]
    public async Task Key_limited_to_other_environments_does_not_endorse_even_an_editor()
    {
        var context = Running();
        AddKey(context, Role.Editor, environments: [EnvironmentId.New()]);

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Equal(EndorsementProblem.KeyOutsideScope, context.Deployment.EndorsementProblem);
    }

    [Fact]
    public async Task Key_limited_to_the_stack_s_environment_endorses()
    {
        var context = Running();
        AddKey(context, Role.Editor, environments: [context.Environment.Id]);

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(CommitEndorsement.Signed, context.Deployment.Endorsement);
    }

    [Fact]
    public async Task Administrator_s_key_endorses_without_a_role()
    {
        var admin = User.CreateAdministrator("root", Now);
        users.Add(admin);
        var context = Running();
        keys.Add(SigningKey.Create(admin.Id, "laptop", PublicKey, null, admin.Id, Now));

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal("root", context.Deployment.SignerName);
    }

    [Fact]
    public async Task Unknown_key_and_invalid_signature_are_rejected_without_naming_the_owner()
    {
        var unknown = Running();
        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(unknown, Ct));
        Assert.Equal(EndorsementProblem.UnknownKey, unknown.Deployment.EndorsementProblem);
        Assert.Contains("no user has added", error.Message, StringComparison.Ordinal);

        var forged = Running();
        AddKey(forged, Role.Editor);
        verifier.Valid = false;
        error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(forged, Ct));
        Assert.Equal(EndorsementProblem.InvalidSignature, forged.Deployment.EndorsementProblem);
        Assert.DoesNotContain("alice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gpg_signature_is_not_accepted()
    {
        var context = Running();
        inspector.Signature = new CommitSignatureData("-----BEGIN PGP SIGNATURE-----\n\niQEz\n-----END PGP SIGNATURE-----\n", []);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Equal(EndorsementProblem.UnsupportedSignature, context.Deployment.EndorsementProblem);
        Assert.Contains("GPG", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approved_unsigned_commit_is_deployed_with_a_warning()
    {
        var context = Running(approved: Commit);
        inspector.Signature = null;

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(CommitEndorsement.ApprovedUnsigned, context.Deployment.Endorsement);
        Assert.Equal(EndorsementProblem.Unsigned, context.Deployment.EndorsementProblem);
        Assert.Contains(Lines(context, DeploymentLogLevel.Warning), l => l.Contains("approved by hand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approval_of_another_commit_does_not_cover_the_branch_head()
    {
        var context = Running(approved: Deployed);
        inspector.Signature = null;

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Equal(EndorsementProblem.ApprovalOutdated, context.Deployment.EndorsementProblem);
        Assert.Contains($"{Deployed.Short} was approved", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeploymentTrigger.Webhook)]
    [InlineData(DeploymentTrigger.DeployHook)]
    public async Task Automatic_deployment_must_descend_from_the_deployed_commit(DeploymentTrigger trigger)
    {
        var context = Running(trigger: trigger, deployed: Deployed);
        AddKey(context, Role.Editor);
        inspector.Descends = false;

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Equal(EndorsementProblem.NotDescendant, context.Deployment.EndorsementProblem);
        Assert.Contains("does not descend", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_deployment_off_the_deployed_line_only_warns()
    {
        var context = Running(deployed: Deployed);
        AddKey(context, Role.Editor);
        inspector.Descends = false;

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(CommitEndorsement.Signed, context.Deployment.Endorsement);
        Assert.Contains(Lines(context, DeploymentLogLevel.Warning), l => l.Contains("does not descend", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Automatic_deployment_of_a_descendant_is_endorsed()
    {
        var context = Running(trigger: DeploymentTrigger.Webhook, deployed: Deployed);
        AddKey(context, Role.Editor);

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(CommitEndorsement.Signed, context.Deployment.Endorsement);
    }

    [Fact]
    public async Task Rehydration_of_the_deployed_commit_is_not_checked_again()
    {
        var context = Running(trigger: DeploymentTrigger.Rehydrate, deployed: Commit);
        inspector.Signature = null;

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(CommitEndorsement.AlreadyDeployed, context.Deployment.Endorsement);
        Assert.Equal(0, inspector.SignatureReads);
    }

    private VerifyCommitStep Step() => new(inspector, verifier, keys, users, roles);

    private SigningKey AddKey(DeployContext context, Role role, IReadOnlyCollection<EnvironmentId>? environments = null)
    {
        roles.Add(RoleAssignment.ForStack(alice.Id, context.Stack, role, Now));
        var key = SigningKey.Create(alice.Id, "laptop", PublicKey, environments, alice.Id, Now);
        keys.Add(key);
        return key;
    }

    /// <summary>A running deployment of <see cref="DeployFixtures.Commit"/> to a stack that requires signed commits.</summary>
    private static DeployContext Running(
        DeploymentTrigger trigger = DeploymentTrigger.Manual, CommitSha? deployed = null, CommitSha? approved = null)
    {
        var manual = Context();
        var stack = manual.Stack;
        stack.SetRequireSignedCommits(true);
        if (deployed is not null)
        {
            var previous = Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Missing, Now.AddHours(-1));
            previous.Start(Now.AddHours(-1));
            previous.ResolveCommit(deployed);
            previous.Succeed(Now.AddHours(-1));
            stack.RecordDeployment(previous, withSecrets: true);
        }

        var deployment = trigger switch
        {
            DeploymentTrigger.Manual => Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Missing, Now, approved),
            DeploymentTrigger.Webhook => Deployment.QueueFromWebhook(stack.Id, Now),
            DeploymentTrigger.DeployHook => Deployment.QueueFromDeployHook(stack.Id, Now),
            _ => Deployment.QueueRehydrate(stack.Id, deployed!, Now),
        };
        deployment.Start(Now);
        if (trigger != DeploymentTrigger.Rehydrate)
        {
            deployment.ResolveCommit(Commit);
        }

        return new DeployContext(deployment, stack, manual.Environment, manual.Repository, new FixedTime(Now));
    }

    private sealed class FakeCommitInspector : ICommitInspector
    {
        public CommitSignatureData? Signature { get; set; } = new(VerifyCommitStepTests.Signature, Encoding.UTF8.GetBytes("tree …"));

        public bool Descends { get; set; } = true;

        public int SignatureReads { get; private set; }

        public Task<CommitSignatureData?> ReadSignatureAsync(GitRepository repository, CommitSha commit, CancellationToken cancellationToken)
        {
            SignatureReads++;
            return Task.FromResult(Signature);
        }

        public Task<bool> DescendsFromAsync(GitRepository repository, CommitSha commit, CommitSha ancestor, CancellationToken cancellationToken) =>
            Task.FromResult(Descends);
    }

    private sealed class FakeSignatureVerifier : ICommitSignatureVerifier
    {
        public bool Valid { get; set; } = true;

        public Task<bool> VerifyAsync(SshPublicKey key, SshSignature signature, byte[] signedData, CancellationToken cancellationToken) =>
            Task.FromResult(Valid && key.Fingerprint == signature.PublicKey.Fingerprint);
    }
}

internal sealed class InMemorySigningKeys : InMemoryRepository<SigningKey, SigningKeyId>, ISigningKeyRepository
{
    public Task<SigningKey?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(k => k.Fingerprint == fingerprint));

    public Task<IReadOnlyList<SigningKey>> ListForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SigningKey>>(Items.Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt).ToList());

    public Task<int> CountForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Count(k => k.UserId == userId));
}
