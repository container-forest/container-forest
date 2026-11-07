using ContainerForest.Aggregator.Domain.Backups;

namespace ContainerForest.Aggregator.Domain.Tests.Backups;

public class BackupScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static BackupRun Succeeded(DateTimeOffset at) => BackupRun.Succeeded(BackupTrigger.Scheduled, at, at, "key", 1);

    private static BackupRun Failed(DateTimeOffset at) => BackupRun.Failed(BackupTrigger.Scheduled, at, at, "reason");

    [Fact]
    public void The_first_backup_is_due_at_once() =>
        Assert.Equal(Now, BackupSchedule.NextRunAt(null, Day, Now));

    [Fact]
    public void After_a_success_the_next_one_waits_a_whole_interval() =>
        Assert.Equal(Now + Day, BackupSchedule.NextRunAt(Succeeded(Now), Day, Now));

    [Fact]
    public void After_a_failure_the_next_one_comes_within_an_hour() =>
        Assert.Equal(Now + BackupSchedule.RetryAfterFailure, BackupSchedule.NextRunAt(Failed(Now), Day, Now));

    [Fact]
    public void The_first_failure_notifies()
    {
        Assert.True(BackupSchedule.ShouldNotifyFailure(null, Now));
        Assert.True(BackupSchedule.ShouldNotifyFailure(Succeeded(Now.AddHours(-1)), Now));
    }

    [Fact]
    public void Repeated_failures_notify_once_a_day()
    {
        Assert.False(BackupSchedule.ShouldNotifyFailure(Failed(Now.AddHours(-1)), Now));
        Assert.True(BackupSchedule.ShouldNotifyFailure(Failed(Now.AddHours(-13)), Now));
    }

    [Fact]
    public void A_long_error_is_cut_to_what_the_column_holds() =>
        Assert.Equal(BackupRun.MaxErrorLength, BackupRun.Failed(BackupTrigger.Manual, Now, Now, new string('x', 2000)).Error!.Length);
}
