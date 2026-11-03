using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Access;

/// <summary>Keys and signatures below were made with OpenSSH's ssh-keygen.</summary>
public class SshKeysTests
{
    public const string Ed25519Key =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINX3MltKY5wuuud0d6OKbOIYmZfhVtfR7HGKjMX7b8BU alice@laptop";

    public const string Ed25519Fingerprint = "SHA256:TrQ9n49hqDp2MBx+ECIov6V5jfq4uTNXkf9sDWJmsyA";

    public const string EcdsaKey =
        "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBF8MPijiji7Wc21jZbdVfgdYSm72ow5oL724OekIOV7/XMe4q9r76yf131Iaz3pVDg5hVM4sMOfOQEajlPurZIo= root@vm";

    public const string Rsa3072Key =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQCt0rzcAwyyW10UMQR+F5r6U9gAyDgZB5WW0C7KVKnSrr02fgUUKbqUepTLSFTUMui5U2p474fbZ5L/gLT/e+xLoo3IPk+3MZ1oAZgBIm6j3bP5gTMGU26CK3z+BWCMtCNIFKPcfKpK43bnyPFWkCNY59Rgr1KZ21wStGM1qTlO7hnKBs0OTATimUOxtsWZ1rWO7Hdl0CcKMcZpgc4+dEJqRrdegctfdMsoqg/zwJbj3WLd5TOc/gya5H9+fM+lJDYOHTdOxn5HaU2it8kwVn1Sf4Lafx+cQN/9POXCsthqx5qOWkiH69YbnsWTbkFFmgovoHnUImO/aws7RRpQs4/9qitEetOZQhBWBwdtN4kPd4x5RuYciFP7xF5N17x0DbnBLI0YaXtaNHU4N0oN4M0nJYlmhMgJGv/VSXmzrwqKkTBwkmpNFb2KynPOqQ0Yyk7zl0YnvO9H4y8L0R7i77cmdId3K6z9SOM0nuVb4slQcIhWAvfZak+NswDSrzn7Qic= root@vm";

    public const string Rsa1024Key =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAAAgQC12G8ydlqbE1ObXgJPp60hhcO5preOn6koJ9A4igxm3UrSVEiNXYLNwgy1frS2VYO2rqIUFyZYfFLxvCE8wdRAjtDfsd9KjJOd81nQZBoXATaFhAzjQ+gHun5K2CWikl9ct67qJhktaCnOXGofCPbo4COi5YIClIebEWwJ4FK9tQ== root@vm";

    /// <summary><c>ssh-keygen -Y sign -n git</c> of "hello" with <see cref="Ed25519Key"/>.</summary>
    public const string GitSignature = """
        -----BEGIN SSH SIGNATURE-----
        U1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAg1fcyW0pjnC6653R3o4ps4hiZl+
        FW19HscYqMxftvwFQAAAADZ2l0AAAAAAAAAAZzaGE1MTIAAABTAAAAC3NzaC1lZDI1NTE5
        AAAAQNvnQrdkjPedsaxUazhjASDKX1Ih3IncXTdE8UZFBxpbesT6oDLtak9yYr4nUZLBit
        kP+Fw7yxLvHFa7iN5miAU=
        -----END SSH SIGNATURE-----
        """;

    [Fact]
    public void Parses_an_ed25519_key_with_the_fingerprint_ssh_keygen_shows()
    {
        var key = SshPublicKey.Parse(Ed25519Key);

        Assert.Equal(SshPublicKey.Ed25519, key.Algorithm);
        Assert.Equal(Ed25519Fingerprint, key.Fingerprint);
        Assert.False(key.IsHardwareBacked);
        Assert.Equal(Ed25519Key[..Ed25519Key.LastIndexOf(' ')], key.OpenSsh);
    }

    [Theory]
    [InlineData(EcdsaKey, "ecdsa-sha2-nistp256")]
    [InlineData(Rsa3072Key, "ssh-rsa")]
    public void Accepts_ecdsa_and_large_rsa_keys(string value, string algorithm) =>
        Assert.Equal(algorithm, SshPublicKey.Parse(value).Algorithm);

    [Theory]
    [InlineData(Rsa1024Key)]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 not-base64!")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAINX3MltKY5wuuud0d6OKbOIYmZfhVtfR7HGKjMX7b8BU")]
    public void Refuses_weak_malformed_and_mislabelled_keys(string value) =>
        Assert.Throws<DomainValidationException>(() => SshPublicKey.Parse(value));

    [Fact]
    public void Reads_the_key_and_namespace_from_a_signature()
    {
        Assert.True(SshSignature.TryParse(GitSignature, out var signature));

        Assert.Equal(SshSignature.GitNamespace, signature.Namespace);
        Assert.Equal("sha512", signature.HashAlgorithm);
        Assert.Equal(Ed25519Fingerprint, signature.PublicKey.Fingerprint);
        Assert.StartsWith("-----BEGIN SSH SIGNATURE-----", signature.Armored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-----BEGIN PGP SIGNATURE-----\n\niQEz\n-----END PGP SIGNATURE-----")]
    [InlineData("-----BEGIN SSH SIGNATURE-----\nbm90IGEgc2lnbmF0dXJl\n-----END SSH SIGNATURE-----")]
    [InlineData("")]
    public void Other_signatures_are_not_ssh_signatures(string value) =>
        Assert.False(SshSignature.TryParse(value, out _));

    [Fact]
    public void Signing_key_scope_only_takes_environments_away()
    {
        var prod = EnvironmentId.New();
        var staging = EnvironmentId.New();
        var everywhere = SigningKey.Create(UserId.New(), "laptop", Ed25519Key, null, UserId.New(), Now);
        var limited = SigningKey.Create(UserId.New(), "ci", EcdsaKey, [staging], UserId.New(), Now);

        Assert.True(everywhere.Scope.Reaches(prod));
        Assert.False(everywhere.Scope.IsReadOnly);
        Assert.True(limited.Scope.Reaches(staging));
        Assert.False(limited.Scope.Reaches(prod));
        Assert.True(limited.WouldWiden([staging, prod]));
        Assert.True(limited.WouldWiden(null));
        Assert.False(limited.WouldWiden([staging]));
        Assert.False(everywhere.WouldWiden([prod]));
    }

    [Fact]
    public void Signing_key_remembers_who_added_it()
    {
        var user = UserId.New();
        var admin = UserId.New();

        Assert.False(SigningKey.Create(user, "laptop", Ed25519Key, null, user, Now).AddedByAdministrator);
        Assert.True(SigningKey.Create(user, "ci", Ed25519Key, null, admin, Now).AddedByAdministrator);
    }

    [Fact]
    public void Signing_key_needs_an_environment_when_limited() =>
        Assert.Throws<DomainValidationException>(() => SigningKey.Create(UserId.New(), "ci", Ed25519Key, [], UserId.New(), Now));
}
