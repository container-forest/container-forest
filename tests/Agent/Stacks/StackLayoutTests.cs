using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;

namespace ContainerForest.Agent.Tests.Stacks;

public class StackLayoutTests
{
    [Theory]
    [InlineData("web")]
    [InlineData("0-app_2")]
    [InlineData("a23456789012345678901234567890123456789012345678901234567890123")]
    public void Valid_stack_names_are_accepted(string name)
    {
        Assert.Equal(name, StackName.Validate(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Web")]
    [InlineData("-web")]
    [InlineData("_web")]
    [InlineData("web\n")]
    [InlineData("web.app")]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")]
    public void Invalid_stack_names_are_rejected(string name)
    {
        var ex = Assert.Throws<InvalidCommandArgumentException>(() => StackName.Validate(name));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Files_keep_their_paths_modes_and_secret_flags()
    {
        var layout = StackLayout.Create("web", [Compose(), File("bin/run.sh", 0b111_101_101), File("secrets/db", 0, secret: true)]);

        Assert.Equal("web", layout.StackName);
        Assert.Equal(["compose.yaml", "bin/run.sh"], layout.RegularFiles.Select(f => f.Path));
        Assert.Equal(StackLayout.ExecutableMode, layout.Files[1].Mode);
        var secret = Assert.Single(layout.SecretFiles);
        Assert.Equal("secrets/db", secret.Path);
        Assert.Equal(StackLayout.RegularMode, secret.Mode);
        Assert.Equal("content of secrets/db", System.Text.Encoding.UTF8.GetString(secret.Content.Span));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside")]
    [InlineData("config/../../outside")]
    [InlineData("./compose.override.yaml")]
    [InlineData("config//app.yaml")]
    [InlineData("config/")]
    [InlineData("..")]
    [InlineData("C:x")]
    [InlineData("config\\..\\..\\x")]
    [InlineData("app.env:stream")]
    [InlineData("line\nbreak")]
    [InlineData("")]
    public void Paths_outside_the_stack_directory_or_ambiguous_ones_are_rejected(string path)
    {
        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.Create("web", [Compose(), File(path)]));
    }

    [Fact]
    public void Duplicate_paths_are_rejected()
    {
        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.Create("web", [Compose(), File("app.env"), File("app.env")]));
    }

    [Fact]
    public void A_path_cannot_be_both_a_file_and_a_directory()
    {
        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.Create("web", [Compose(), File("config"), File("config/app.yaml")]));
    }

    [Fact]
    public void Compose_file_is_required()
    {
        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.Create("web", [File("docker-compose.yml")]));
    }

    [Fact]
    public void Compose_file_cannot_be_a_secret()
    {
        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.Create("web", [File("compose.yaml", secret: true)]));
    }

    [Theory]
    [InlineData(0b100_111_101_101u)] // setuid 4755
    [InlineData(0b111_111_111u)]     // 0777
    [InlineData(0b110_000_000u)]     // 0600
    public void Modes_other_than_644_and_755_are_rejected(uint mode)
    {
        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.Create("web", [Compose(), File("run.sh", mode)]));
    }

    [Fact]
    public void Invalid_stack_name_is_rejected_before_the_files()
    {
        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.Create("Web", [Compose()]));
    }

    [Fact]
    public void Resolve_returns_the_path_inside_the_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stacks", "web");

        Assert.Equal(Path.Combine(directory, "config", "app.yaml"), StackLayout.ResolveIn(directory, "config/app.yaml"));
    }

    [Theory]
    [InlineData("../web2/compose.yaml")]
    [InlineData("..")]
    [InlineData(".")]
    public void Resolve_rejects_paths_outside_the_directory(string path)
    {
        var directory = Path.Combine(Path.GetTempPath(), "stacks", "web");

        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.ResolveIn(directory, path));
    }

    [Fact]
    public void Resolve_rejects_a_sibling_directory_with_the_same_prefix()
    {
        // "stacks/web-old" starts with "stacks/web" as a string, but it is another stack's directory.
        var directory = Path.Combine(Path.GetTempPath(), "stacks", "web");

        Assert.Throws<InvalidCommandArgumentException>(() => StackLayout.ResolveIn(directory, "../web-old/compose.yaml"));
    }

    private static StackFile Compose() => File(StackLayout.ComposeFileName);

    private static StackFile File(string path, uint mode = 0b110_100_100, bool secret = false) => new()
    {
        Path = path,
        Content = ByteString.CopyFromUtf8($"content of {path}"),
        Mode = mode,
        Secret = secret,
    };
}
