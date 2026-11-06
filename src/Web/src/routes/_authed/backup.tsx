import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { useRunBackupMutation } from "@/api/backup";
import { backupSettingsQuery, backupStatusQuery } from "@/api/queries";
import { BackupSettingsForm } from "@/components/backup/backup-settings-form";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { formatSize } from "@/features/backup/model";
import { describeError } from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";

export const Route = createFileRoute("/_authed/backup")({
  loader: ({ context }) =>
    Promise.all([context.queryClient.ensureQueryData(backupStatusQuery), context.queryClient.ensureQueryData(backupSettingsQuery)]),
  component: BackupScreen,
  errorComponent: BackupLoadError,
});

/** The aggregator's backup for administrators: whether it works, the latest runs, and the settings. */
function BackupScreen() {
  const { t, i18n } = useTranslation();
  // A run may end at any moment, and nothing announces it: reading now and then is enough for this screen.
  const { data: status } = useSuspenseQuery({ ...backupStatusQuery, refetchInterval: 30_000 });
  const settings = useQuery(backupSettingsQuery);
  const run = useRunBackupMutation();
  const runError = run.error ? describeError(run.error) : undefined;
  const time = (value: string | null | undefined) => (value ? formatDateTime(value, i18n.language) : t("backup.status.never"));

  return (
    <div className="grid max-w-4xl gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">{t("backup.title")}</h1>
        <p className="text-muted-foreground mt-1">{t("backup.description")}</p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            {t("backup.status.title")}
            <Badge variant={status.enabled ? "default" : "secondary"}>{t(status.enabled ? "backup.status.on" : "backup.status.off")}</Badge>
          </CardTitle>
          <CardDescription>{t("backup.status.description")}</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-[auto_1fr]">
            <dt className="text-muted-foreground">{t("backup.status.lastSuccess")}</dt>
            <dd>{time(status.lastSucceededAt)}</dd>
            <dt className="text-muted-foreground">{t("backup.status.nextRun")}</dt>
            <dd>{status.nextRunAt ? formatDateTime(status.nextRunAt, i18n.language) : "—"}</dd>
          </dl>

          {status.problems.length > 0 && (
            <div className="grid gap-1">
              <p className="text-sm font-medium">{t("backup.status.problems")}</p>
              {status.problems.map((problem) => (
                <ErrorText key={problem}>{problem}</ErrorText>
              ))}
            </div>
          )}

          {(!status.notifiesFailures || !status.notifiesChanges) && (
            <p className="text-sm">
              {t("backup.status.noRule")}{" "}
              <Link to="/notifications" className="underline">
                {t("backup.status.noRuleLink")}
              </Link>
            </p>
          )}

          {runError && (
            <ErrorText>
              {t(runError.key)} {runError.detail}
            </ErrorText>
          )}
          {run.data?.error && <ErrorText>{run.data.error}</ErrorText>}

          <div>
            <Button variant="outline" disabled={run.isPending || status.isRunning} onClick={() => run.mutate()}>
              {(run.isPending || status.isRunning) && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("backup.status.runNow")}
            </Button>
          </div>
        </CardContent>
      </Card>

      {settings.data && <BackupSettingsForm settings={settings.data} />}

      <Card>
        <CardHeader>
          <CardTitle>{t("backup.runs.title")}</CardTitle>
        </CardHeader>
        <CardContent>
          {status.runs.length === 0 ? (
            <p className="text-muted-foreground text-sm">{t("backup.runs.empty")}</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("backup.runs.finished")}</TableHead>
                  <TableHead>{t("backup.runs.outcome")}</TableHead>
                  <TableHead className="hidden md:table-cell">{t("backup.runs.trigger")}</TableHead>
                  <TableHead className="hidden md:table-cell">{t("backup.runs.size")}</TableHead>
                  <TableHead>{t("backup.runs.result")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {status.runs.map((entry) => (
                  <TableRow key={entry.id}>
                    <TableCell className="whitespace-nowrap">{formatDateTime(entry.finishedAt, i18n.language)}</TableCell>
                    <TableCell>
                      <Badge variant={entry.outcome === "Succeeded" ? "secondary" : "destructive"}>
                        {t(entry.outcome === "Succeeded" ? "backup.runs.succeeded" : "backup.runs.failed")}
                      </Badge>
                    </TableCell>
                    <TableCell className="hidden md:table-cell">
                      {t(entry.trigger === "Manual" ? "backup.runs.manual" : "backup.runs.scheduled")}
                    </TableCell>
                    <TableCell className="hidden md:table-cell">{formatSize(entry.sizeBytes)}</TableCell>
                    <TableCell className="break-all whitespace-normal">
                      {entry.error ?? <span className="font-mono text-sm">{entry.objectKey}</span>}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function BackupLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();
  const { key, detail } = describeError(error);

  return (
    <ErrorText>
      {t("backup.loadFailed")} {t(key)} {detail}
    </ErrorText>
  );
}
