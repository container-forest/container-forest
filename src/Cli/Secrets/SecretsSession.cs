using ContainerForest.Cli.Commands;
using ContainerForest.Cli.Configuration;

namespace ContainerForest.Cli.Secrets;

/// <summary>
/// The git filter's view of the repository and of the CLI: which context it talks to (<c>forest.context</c>), whether
/// sops runs locally (<c>forest.localSops</c>), and the cache. The aggregator is reached only when a file needs it.
/// </summary>
internal sealed class SecretsSession
{
    private SecretsSession(GitWorkTree tree, FilterServices services)
    {
        Tree = tree;
        Services = services;
    }

    public GitWorkTree Tree { get; }

    public FilterServices Services { get; }

    /// <param name="useCache">False for textconv, which only shows files and must not change what clean produces.</param>
    public static SecretsSession Open(CliHost host, TextWriter error, bool useCache = true)
    {
        var tree = GitWorkTree.Open(host.WorkingDirectory);
        var contextName = tree.GetConfig(GitFilterSetup.ContextKey);
        var aggregator = new AggregatorSops(
            () => host.ApiFactory.Create(host.Credentials.Resolve(new CredentialOptions(null, null, null, contextName))),
            tree.Root);

        IReadOnlyList<ISopsBackend> decryptors = [aggregator];
        ISopsBackend encryptor = aggregator;
        if (string.Equals(tree.GetConfig(GitFilterSetup.LocalSopsKey), "true", StringComparison.OrdinalIgnoreCase))
        {
            var local = new LocalSops(tree.GetConfig(GitFilterSetup.SopsPathKey) ?? "sops", tree.Root);
            decryptors = [local, aggregator];
            encryptor = local;
        }

        var cache = useCache ? FilterCache.Open(tree.GitDirectory) : null;
        return new SecretsSession(tree, new FilterServices(decryptors, encryptor, cache, error));
    }
}
