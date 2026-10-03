namespace ContainerForest.Aggregator.Infrastructure.Secrets;

/// <summary>Configuration section "Sops" (env: CF_SOPS__...).</summary>
public sealed class SopsOptions
{
    public const string SectionName = "Sops";

    /// <summary>The sops binary; a bare name is looked up in PATH. The aggregator image has it.</summary>
    public string SopsPath { get; set; } = "sops";

    /// <summary>The age-keygen binary; a bare name is looked up in PATH. The aggregator image has it.</summary>
    public string AgeKeygenPath { get; set; } = "age-keygen";

    /// <summary>How long one sops or age-keygen run may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsValid() => Timeout > TimeSpan.Zero && SopsPath.Length > 0 && AgeKeygenPath.Length > 0;
}
