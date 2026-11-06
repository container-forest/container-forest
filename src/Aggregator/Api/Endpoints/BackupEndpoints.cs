using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Backups;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Backups;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>The settings and the state of the aggregator's backup (see BackupContracts).</summary>
internal static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/backup", async (BackupSettingsService service, CancellationToken ct) =>
                ToResponse(await service.GetAsync(ct)))
            .WithTags("Backup")
            .WithName("GetBackupSettings")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPut("/api/settings/backup", async (UpdateBackupSettingsRequest request, BackupSettingsService service, CancellationToken ct) =>
                ToResponse(await service.UpdateAsync(
                    new BackupSettingsUpdate(
                        request.Enabled,
                        request.IntervalHours,
                        request.Recipient,
                        request.Endpoint,
                        request.Bucket,
                        request.Region,
                        request.Prefix,
                        request.AccessKeyId,
                        request.SecretAccessKey),
                    request.Confirmation,
                    ct)))
            .WithTags("Backup")
            .WithName("UpdateBackupSettings")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapGet("/api/backup", async (BackupService service, CancellationToken ct) =>
            {
                var status = await service.GetStatusAsync(ct);
                return new BackupStatusResponse(
                    status.Enabled,
                    status.Problems,
                    status.IsRunning,
                    status.NextRunAt,
                    status.LastSucceededAt,
                    status.NotifiesFailures,
                    status.NotifiesChanges,
                    status.Runs.Select(ToResponse).ToList());
            })
            .WithTags("Backup")
            .WithName("GetBackupStatus")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPost("/api/backup/run", async (BackupService service, CancellationToken ct) =>
                ToResponse(await service.RunNowAsync(ct)))
            .WithTags("Backup")
            .WithName("RunBackup")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static BackupRunResponse ToResponse(BackupRun run) =>
        new(run.Id.Value, run.Trigger, run.Outcome, run.StartedAt, run.FinishedAt, run.ObjectKey, run.SizeBytes, run.Error);

    private static BackupSettingsResponse ToResponse(ResolvedSettings settings)
    {
        SettingTextResponse Text(string field)
        {
            var setting = settings[field];
            return new SettingTextResponse(setting.Value, setting.Source, setting.IsLocked, setting.Problem);
        }

        var secret = settings[BackupSettingsSection.SecretAccessKeyField];
        return new BackupSettingsResponse(
            Text(BackupSettingsSection.EnabledField),
            Text(BackupSettingsSection.IntervalHoursField),
            Text(BackupSettingsSection.RecipientField),
            Text(BackupSettingsSection.EndpointField),
            Text(BackupSettingsSection.BucketField),
            Text(BackupSettingsSection.RegionField),
            Text(BackupSettingsSection.PrefixField),
            Text(BackupSettingsSection.AccessKeyIdField),
            new SettingSecretResponse(secret.HasValue, secret.Source, secret.IsLocked, secret.Problem));
    }
}
