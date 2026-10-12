using System.Text;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging.Abstractions;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Secrets;

public class SopsSecretsServiceTests
{
    private const string ProdRecipient = "age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng";
    private const string StagingRecipient = "age1qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqs3290gq";
    private const string PersonalRecipient = "age1lggyhqrw2nlhcxprm67z43rta597azn8gknawjehu9d9dl0jq3yqqvfafg";

    private readonly InMemoryUsers users = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryAgeKeys keys = new();
    private readonly FakeSecretProtector protector = new();
    private readonly FakeSopsDecryptor decryptor = new();
    private readonly FakeSopsEncryptor encryptor = new();
    private readonly FakeCurrentUser currentUser = new();

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment prod = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly DockerEnvironment staging = DockerEnvironment.Create("staging", isPrivate: false, Now);

    public SopsSecretsServiceTests()
    {
        users.Add(admin);
        users.Add(alice);
        keys.Add(AgeKey.Create(prod.Id, ProdRecipient, protector.Protect(SecretPurpose.AgeIdentity, "prod-identity"), Now));
        keys.Add(AgeKey.Create(staging.Id, StagingRecipient, protector.Protect(SecretPurpose.AgeIdentity, "staging-identity"), Now));
        currentUser.Id = alice.Id;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Editor_decrypts_with_the_keys_of_their_environments_only()
    {
        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, staging.Id, Role.Editor, Now));
        var file = Encrypted("staging-identity", "TOKEN=s3cret", StagingRecipient, ProdRecipient);

        var plain = await Service().DecryptAsync(Env, file, Ct);

        Assert.Equal("TOKEN=s3cret", Encoding.UTF8.GetString(plain));
        Assert.Equal(["staging-identity"], Assert.Single(decryptor.Calls).Identities);
    }

    [Fact]
    public async Task Editor_of_one_stack_counts_as_an_editor_of_its_environment()
    {
        var stack = Stack.Create(
            prod.Id, StackName.Parse("web"), RepositoryId.New(), BranchName.Parse("main"), StackFilePath.Parse("compose.yaml"), isSystem: false, Now);
        roleAssignments.Add(RoleAssignment.ForStack(alice.Id, stack, Role.Editor, Now));

        await Service().DecryptAsync(Env, Encrypted("prod-identity", "x", ProdRecipient), Ct);

        Assert.Equal(["prod-identity"], Assert.Single(decryptor.Calls).Identities);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Operator)]
    public async Task Anyone_but_an_editor_is_denied_before_sops_runs(Role? role)
    {
        if (role is { } granted)
        {
            roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, prod.Id, granted, Now));
        }

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            Service().DecryptAsync(Env, Encrypted("prod-identity", "x", ProdRecipient), Ct));
        Assert.Empty(decryptor.Calls);
    }

    [Fact]
    public async Task Administrator_decrypts_files_of_any_environment()
    {
        currentUser.Id = admin.Id;

        await Service().DecryptAsync(Env, Encrypted("prod-identity", "x", ProdRecipient, StagingRecipient), Ct);

        Assert.Equal(2, Assert.Single(decryptor.Calls).Identities.Count);
    }

    [Fact]
    public async Task Token_needs_the_right_to_decrypt_secrets()
    {
        roleAssignments.Add(RoleAssignment.ForEnvironment(alice.Id, prod.Id, Role.Editor, Now));
        var file = Encrypted("prod-identity", "x", ProdRecipient);

        currentUser.UseToken(ApiToken.Create(alice.Id, "ci", readOnly: false, null, null, Now).Token);
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => Service().DecryptAsync(Env, file, Ct));
        Assert.Contains("--secrets", error.Message, StringComparison.Ordinal);

        currentUser.UseToken(ApiToken.Create(alice.Id, "git", readOnly: false, null, null, Now, canDecryptSecrets: true).Token);
        await Service().DecryptAsync(Env, file, Ct);
    }

    [Fact]
    public async Task Token_limited_to_other_environments_does_not_decrypt_even_for_an_administrator()
    {
        currentUser.UseToken(ApiToken.Create(admin.Id, "git", readOnly: false, [staging.Id], null, Now, canDecryptSecrets: true).Token);

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            Service().DecryptAsync(Env, Encrypted("prod-identity", "x", ProdRecipient), Ct));
        await Service().DecryptAsync(Env, Encrypted("staging-identity", "x", StagingRecipient, ProdRecipient), Ct);
        Assert.Equal(["staging-identity"], Assert.Single(decryptor.Calls).Identities);
    }

    [Fact]
    public async Task File_for_no_environment_key_cannot_be_decrypted_here()
    {
        currentUser.Id = admin.Id;

        await Assert.ThrowsAsync<SopsDecryptionException>(() =>
            Service().DecryptAsync(Env, Encrypted("personal", "x", PersonalRecipient), Ct));
    }

    [Fact]
    public async Task Plain_file_is_not_decrypted()
    {
        currentUser.Id = admin.Id;

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Service().DecryptAsync(Env, Encoding.UTF8.GetBytes($"KEY={ProdRecipient}\n"), Ct));
    }

    [Fact]
    public async Task Encrypts_with_the_rule_of_the_config_for_the_path()
    {
        var encrypted = await Service().EncryptAsync(Env, "TOKEN=x\n"u8.ToArray(), "rules", Ct);

        Assert.Equal("encrypted .env", Encoding.UTF8.GetString(encrypted));
        Assert.Equal([ProdRecipient], Assert.Single(encryptor.Rules).AgeRecipients);
    }

    [Fact]
    public async Task Read_only_token_does_not_encrypt_and_encrypted_content_is_refused()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Service().EncryptAsync(Env, Encrypted("prod-identity", "x", ProdRecipient), "rules", Ct));

        currentUser.UseToken(ApiToken.Create(alice.Id, "ro", readOnly: true, null, null, Now).Token);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service().EncryptAsync(Env, "TOKEN=x\n"u8.ToArray(), "rules", Ct));
        Assert.Empty(encryptor.Rules);
    }

    // One per request, as in the aggregator: CurrentActor keeps the actor of its request.
    private SopsSecretsService Service() => new(
        new CurrentActor(currentUser, users),
        roleAssignments,
        keys,
        protector,
        decryptor,
        encryptor,
        new FakeSopsConfigReader(),
        new FakeAuditTrail(),
        NullLogger<SopsSecretsService>.Instance);

    private static StackFilePath Env => StackFilePath.Parse("secrets/.env");

    // Dotenv metadata with the recipients sops writes; the fake decryptor reads the rest.
    private static byte[] Encrypted(string identity, string plainText, params string[] recipients) =>
        Encoding.UTF8.GetBytes(
            $"encrypted for {identity}: {plainText}\n" +
            string.Concat(recipients.Select((r, i) => $"sops_age__list_{i}__map_recipient={r}\n")) +
            "sops_version=3.13.3\n");

    private sealed class FakeSopsEncryptor : ISopsEncryptor
    {
        public List<SopsEncryptionRule> Rules { get; } = [];

        public Task<byte[]> EncryptAsync(StackFilePath path, byte[] content, SopsEncryptionRule rule, CancellationToken cancellationToken)
        {
            Rules.Add(rule);
            return Task.FromResult(Encoding.UTF8.GetBytes($"encrypted {path.FileName}"));
        }
    }

    private sealed class FakeSopsConfigReader : ISopsConfigReader
    {
        public SopsEncryptionRule RuleFor(string config, StackFilePath path) => new([ProdRecipient]);
    }
}
