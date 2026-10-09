using ContainerForest.Cli.Configuration;

namespace ContainerForest.Cli.Tests.Configuration;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"forest-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_is_an_empty_configuration()
    {
        var config = new ConfigStore(root).Load();

        Assert.Empty(config.Contexts);
        Assert.Null(config.Current);
    }

    [Fact]
    public void Saved_configuration_reads_back()
    {
        var store = new ConfigStore(Path.Combine(root, "container-forest"));
        var config = new CliConfig { CurrentContext = "r2" };
        config.Contexts["r2"] = new ContextEntry { Url = "https://forest.example.com/", Token = "cf_pat_x", UserName = "root", Environment = "r2" };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal("r2", loaded.CurrentContext);
        Assert.Equal("cf_pat_x", loaded.Current!.Token);
        Assert.Equal("r2", loaded.Current.Environment);
        Assert.Equal([ConfigStore.FileName], Directory.GetFiles(store.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void File_with_tokens_is_private_to_its_owner()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions only.");
        var store = new ConfigStore(Path.Combine(root, "container-forest"));

        store.Save(new CliConfig());

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.Directory));
    }

    [Fact]
    public void Broken_file_is_reported_with_its_path()
    {
        Directory.CreateDirectory(root);
        var store = new ConfigStore(root);
        File.WriteAllText(store.FilePath, "{ not json");

        var broken = Assert.Throws<CliException>(store.Load);

        Assert.Contains(store.FilePath, broken.Message);
    }

    [Fact]
    public void Default_place_follows_xdg_config_home()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "XDG is for Unix.");
        var variables = new Dictionary<string, string?> { ["XDG_CONFIG_HOME"] = "/xdg", ["HOME"] = "/home/u" };

        Assert.Equal("/xdg/container-forest", ConfigStore.Default(n => variables.GetValueOrDefault(n)).Directory);
    }

    [Fact]
    public void Default_place_without_xdg_is_dot_config()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "XDG is for Unix.");
        var variables = new Dictionary<string, string?> { ["XDG_CONFIG_HOME"] = "relative/is/ignored", ["HOME"] = "/home/u" };

        Assert.Equal("/home/u/.config/container-forest", ConfigStore.Default(n => variables.GetValueOrDefault(n)).Directory);
    }
}
