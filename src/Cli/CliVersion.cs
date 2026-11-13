using System.Reflection;

namespace ContainerForest.Cli;

internal static class CliVersion
{
    /// <summary>The release version from Directory.Build.props, e.g. "0.8.0".</summary>
    public static string Current { get; } =
        typeof(CliVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
