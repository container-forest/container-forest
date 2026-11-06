using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    public async Task<BackupStatusResponse> GetBackupStatusAsync(CancellationToken cancellationToken) =>
        Required(await Api.Backup.GetAsync(cancellationToken: cancellationToken), "backup status");

    public async Task<BackupRunResponse> RunBackupAsync(CancellationToken cancellationToken) =>
        Required(await Api.Backup.Run.PostAsync(cancellationToken: cancellationToken), "backup run");
}
