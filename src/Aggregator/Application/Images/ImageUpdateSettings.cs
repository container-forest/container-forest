using System.Collections.Concurrent;
using System.Globalization;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Images;

/// <summary>Configuration section "ImageUpdates" (env: CF_IMAGEUPDATES__...): limits an administrator rarely changes.</summary>
public sealed class ImageUpdateOptions
{
    public const string SectionName = "ImageUpdates";

    /// <summary>Manifests and configurations one check of an <c>ImageCreated</c> policy reads; the rest wait for the next check.</summary>
    public int MaxTagReadsPerCheck { get; set; } = 100;

    /// <summary>How long one registry request may take.</summary>
    public TimeSpan RegistryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The author and committer of update commits; a git server shows them as verified when the email is its service user's.</summary>
    public string AuthorName { get; set; } = "container-forest";

    public string AuthorEmail { get; set; } = "container-forest@localhost";

    /// <summary>Lets registries on private, loopback and link-local addresses be read; off by default, as for git.</summary>
    public bool AllowPrivateRegistries { get; set; }

    public bool IsValid() =>
        MaxTagReadsPerCheck > 0 && RegistryTimeout > TimeSpan.Zero
        && !string.IsNullOrWhiteSpace(AuthorName) && AuthorEmail.Contains('@', StringComparison.Ordinal)
        && !AuthorName.Any(c => char.IsControl(c) || c is '<' or '>') && !AuthorEmail.Any(c => char.IsControl(c) || c is '<' or '>' or ' ');
}

/// <summary>Polling of registries (ADR-0015, item 2), set from the panel: on by default, every five minutes.</summary>
public sealed class ImageUpdateSettingsSection : SettingsSection
{
    public const string SectionName = "ImageUpdates";
    public const string PollingEnabledField = "PollingEnabled";
    public const string PollingIntervalMinutesField = "PollingIntervalMinutes";
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;
    public const int DefaultIntervalMinutes = 5;

    public static readonly ImageUpdateSettingsSection Instance = new();

    public override string Name => SectionName;

    public override IReadOnlyList<SettingField> Fields { get; } =
    [
        new(PollingEnabledField, Default: "true", Normalize: NormalizeFlag),
        new(PollingIntervalMinutesField, Default: DefaultIntervalMinutes.ToString(CultureInfo.InvariantCulture), Normalize: NormalizeInterval),
    ];

    private static string NormalizeFlag(string value) =>
        bool.TryParse(value, out var flag)
            ? flag ? "true" : "false"
            : throw new DomainValidationException("The value must be true or false.");

    private static string NormalizeInterval(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= MinIntervalMinutes and <= MaxIntervalMinutes
            ? minutes.ToString(CultureInfo.InvariantCulture)
            : throw new DomainValidationException($"The interval must be from {MinIntervalMinutes} to {MaxIntervalMinutes} minutes.");
}

/// <summary>The typed view of <see cref="ImageUpdateSettingsSection"/>.</summary>
public sealed record ImageUpdateSettings(bool PollingEnabled, TimeSpan PollingInterval)
{
    public static ImageUpdateSettings From(ResolvedSettings settings)
    {
        var minutes = int.TryParse(settings[ImageUpdateSettingsSection.PollingIntervalMinutesField].Value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : ImageUpdateSettingsSection.DefaultIntervalMinutes;
        return new ImageUpdateSettings(
            settings[ImageUpdateSettingsSection.PollingEnabledField].Value != "false",
            TimeSpan.FromMinutes(Math.Clamp(minutes, ImageUpdateSettingsSection.MinIntervalMinutes, ImageUpdateSettingsSection.MaxIntervalMinutes)));
    }
}

/// <summary>
/// Updates of one stack run one at a time, whoever starts them: polling, the image hook or a user. One aggregator runs
/// per system, so a lock in memory is enough. Singleton.
/// </summary>
public sealed class ImageUpdateLocks
{
    private readonly ConcurrentDictionary<StackId, SemaphoreSlim> stacks = new();

    /// <summary>Held while the aggregator's signing key is created or rotated, so there is only ever one active.</summary>
    public SemaphoreSlim SigningKey { get; } = new(1, 1);

    /// <summary>Holds the stack until the returned value is disposed.</summary>
    public async Task<IDisposable> EnterAsync(StackId stackId, CancellationToken cancellationToken)
    {
        var gate = stacks.GetOrAdd(stackId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
