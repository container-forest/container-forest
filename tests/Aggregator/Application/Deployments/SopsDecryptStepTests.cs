using System.Text;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class SopsDecryptStepTests
{
    private const string OldRecipient = "age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng";
    private const string NewRecipient = "age1qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqs3290gq";

    private readonly InMemoryAgeKeys keys = new();
    private readonly FakeSecretProtector protector = new();
    private readonly FakeSopsDecryptor sops = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Files_without_sops_metadata_pass_through_without_keys()
    {
        var context = Fetched("compose.yaml", File("compose.yaml", "services: {}"), File("README.md", "uses sops: see docs"));

        await Step().ExecuteAsync(context, Ct);

        Assert.Empty(sops.Calls);
        Assert.All(context.Source.Files, f => Assert.False(f.IsSecret));
    }

    [Fact]
    public async Task Encrypted_files_are_decrypted_with_all_keys_of_the_environment_and_marked_secret()
    {
        var context = Fetched("compose.yaml", File("compose.yaml", "services: {}"), Encrypted(".env", "old-identity", "TOKEN=s3cret"));
        AddKey(context.Environment, OldRecipient, "old-identity", Now.AddDays(-1));
        AddKey(context.Environment, NewRecipient, "new-identity", Now);

        await Step().ExecuteAsync(context, Ct);

        var (path, identities) = Assert.Single(sops.Calls);
        Assert.Equal(".env", path.Value);
        Assert.Equal(["new-identity", "old-identity"], identities);
        var secret = Assert.Single(context.Source.Files, f => f.IsSecret);
        Assert.Equal("TOKEN=s3cret", Encoding.UTF8.GetString(secret.Content));
        Assert.False(context.Source.Files.Single(f => f.Path.Value == "compose.yaml").IsSecret);
    }

    [Fact]
    public async Task Log_names_the_files_but_never_their_content()
    {
        var context = Fetched("compose.yaml", File("compose.yaml", "services: {}"), Encrypted("secrets/db.yaml", "identity", "password: hunter2"));
        AddKey(context.Environment, OldRecipient, "identity", Now);

        await Step().ExecuteAsync(context, Ct);

        var line = Assert.Single(Lines(context, DeploymentLogLevel.Info));
        Assert.Contains("secrets/db.yaml", line, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Deployment.Log, l => l.Text.Contains("hunter2", StringComparison.Ordinal)
            || l.Text.Contains("identity:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Environment_without_a_key_fails_with_what_to_do()
    {
        var context = Fetched("compose.yaml", Encrypted(".env", "identity", "TOKEN=x"));

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Contains("has no age key", error.Message, StringComparison.Ordinal);
        Assert.Contains(".env", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_of_other_environments_are_not_used()
    {
        var context = Fetched("compose.yaml", Encrypted(".env", "other-identity", "TOKEN=x"));
        AddKey(context.Environment, OldRecipient, "identity", Now);
        var other = DockerEnvironment.Create("other", isPrivate: false, Now);
        AddKey(other, NewRecipient, "other-identity", Now);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.StartsWith("Could not decrypt '.env': none of this environment's age keys", error.Message, StringComparison.Ordinal);
        Assert.Equal(["identity"], Assert.Single(sops.Calls).Identities);
    }

    [Fact]
    public async Task Unreadable_private_key_fails_the_deployment()
    {
        var context = Fetched("compose.yaml", Encrypted(".env", "identity", "TOKEN=x"));
        keys.Add(AgeKey.Create(context.Environment.Id, OldRecipient, "protected with a lost key ring", Now));

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Contains(OldRecipient, error.Message, StringComparison.Ordinal);
        Assert.Empty(sops.Calls);
    }

    private SopsDecryptStep Step() => new(keys, protector, sops);

    private void AddKey(DockerEnvironment environment, string recipient, string identity, DateTimeOffset createdAt) =>
        keys.Add(AgeKey.Create(environment.Id, recipient, protector.Protect(SecretPurpose.AgeIdentity, identity), createdAt));

    // Dotenv metadata makes the file count as SOPS-encrypted; the fake decryptor reads the rest.
    private static SourceFile Encrypted(string path, string identity, string plainText) => path.EndsWith(".yaml", StringComparison.Ordinal)
        ? File(path, $"data: encrypted for {identity}: {plainText}\nsops:\n    version: 3.13.3\n")
        : File(path, $"encrypted for {identity}: {plainText}\nsops_version=3.13.3\n");
}
