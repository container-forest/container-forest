using System.Reflection;

namespace ContainerForest.Cli;

internal static class CliVersion
{
    /// <summary>The version from the project file, e.g. "0.1.0".</summary>
    public static string Current { get; } =
        typeof(CliVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
