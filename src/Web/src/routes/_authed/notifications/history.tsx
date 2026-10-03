import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { notificationChannelsQuery, notificationsQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { FilterXIcon, Loader2Icon, RefreshCwIcon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  DEFAULT_HISTORY_LIMIT,
  type HistorySearch,
  PENDING_POLL_MS,
  countByStatus,
  describeError,
  filterNotifications,
  hasPending,
  historyChannels,
  historyLimits,
  historySearchSchema,
  isKnownKind,
  notificationStatuses,
  severityVariant,
  statusVariant,
} from "@/features/notifications/model";
import { formatDateTime } from "@/lib/format";

/** Radix Select cannot hold an empty value; "all" stands for "no filter", which the URL leaves out. */
const ALL = "all";

export const Route = createFileRoute("/_authed/notifications/history")({
  validateSearch: historySearchSchema,
  loaderDeps: ({ search }) => ({ limit: search.limit ?? DEFAULT_HISTORY_LIMIT }),
  loader: ({ context, deps }) => context.queryClient.ensureQueryData(notificationsQuery(deps.limit)),
  component: HistoryScreen,
  errorComponent: HistoryLoadError,
});

/** Every notification sent (or not) to the channels, newest first, with why delivery failed. */
function HistoryScreen() {
  const { t, i18n } = useTranslation();
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const limit = search.limit ?? DEFAULT_HISTORY_LIMIT;
  const notifications = useSuspenseQuery({
    ...notificationsQuery(limit),
    // Pending records turn Delivered or Failed once the bot answers; there is no live channel yet.
    refetchInterval: (query) => (query.state.data && hasPending(query.state.data) ? PENDING_POLL_MS : false),
  });
  // Only names for the channel filter: the history still shows without them.
  const channels = useQuery(notificationChannelsQuery);
  const channelOptions = historyChannels(channels.data ?? [], notifications.data);
  const counts = countByStatus(notifications.data);
  const shown = filterNotifications(notifications.data, search);
  const filtered = search.status !== undefined || search.channel !== undefined;

  // Defaults are left out of the URL; replace keeps filter changes out of the history.
  const setSearch = (next: Partial<HistorySearch>) =>
    navigate({
      search: (previous) => {
        const merged = { ...previous, ...next };
        return {
          status: merged.status,
          channel: merged.channel,
          limit: merged.limit === DEFAULT_HISTORY_LIMIT ? undefined : merged.limit,
        };
      },
      replace: true,
    });

  return (
    <div className="grid gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">{t("notifications.history.title")}</h1>
        <p className="text-muted-foreground mt-1 max-w-2xl">{t("notifications.history.description")}</p>
      </div>

      <div className="flex flex-wrap items-end gap-4">
        <div className="grid gap-1.5">
          <Label htmlFor="history-status">{t("notifications.history.filters.status")}</Label>
          <Select
            value={search.status ?? ALL}
            onValueChange={(value) =>
              setSearch({ status: value === ALL ? undefined : notificationStatuses.find((s) => s === value) })
            }
          >
            <SelectTrigger id="history-status" className="w-48">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>{t("notifications.history.filters.allStatuses")}</SelectItem>
              {notificationStatuses.map((status) => (
                <SelectItem key={status} value={status}>
                  {t(`notifications.status.${status}`)} ({counts[status]})
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="grid gap-1.5">
          <Label htmlFor="history-channel">{t("notifications.history.filters.channel")}</Label>
          <Select value={search.channel ?? ALL} onValueChange={(value) => setSearch({ channel: value === ALL ? undefined : value })}>
            <SelectTrigger id="history-channel" className="w-56">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>{t("notifications.history.filters.allChannels")}</SelectItem>
              {channelOptions.map((channel) => (
                <SelectItem key={channel.id} value={channel.id}>
                  {channel.name ?? t("notifications.history.deletedChannel")}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="grid gap-1.5">
          <Label htmlFor="history-limit">{t("notifications.history.filters.limit")}</Label>
          <Select value={String(limit)} onValueChange={(value) => setSearch({ limit: historyLimits.find((l) => String(l) === value) })}>
            <SelectTrigger id="history-limit" className="w-28">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {historyLimits.map((value) => (
                <SelectItem key={value} value={String(value)}>
                  {value}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        {filtered && (
          <Button variant="ghost" onClick={() => setSearch({ status: undefined, channel: undefined })}>
            <FilterXIcon data-icon="inline-start" />
            {t("notifications.history.filters.clear")}
          </Button>
        )}

        <Button variant="outline" disabled={notifications.isFetching} onClick={() => void notifications.refetch()}>
          {notifications.isFetching ? (
            <Loader2Icon data-icon="inline-start" className="animate-spin" />
          ) : (
            <RefreshCwIcon data-icon="inline-start" />
          )}
          {t("notifications.history.refresh")}
        </Button>
      </div>

      {notifications.isRefetchError && (
        <ErrorText>
          {t("notifications.history.refreshFailed")} {t(describeError(notifications.error).key)}
        </ErrorText>
      )}

      {shown.length === 0 ? (
        <p className="text-muted-foreground">
          {filtered ? t("notifications.history.emptyFiltered") : t("notifications.history.empty")}
        </p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("notifications.history.columns.occurred")}</TableHead>
              <TableHead>{t("notifications.history.columns.channel")}</TableHead>
              <TableHead>{t("notifications.history.columns.event")}</TableHead>
              <TableHead>{t("notifications.history.columns.summary")}</TableHead>
              <TableHead>{t("notifications.history.columns.status")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.map((n) => (
              <TableRow key={n.id}>
                <TableCell className="text-sm whitespace-nowrap">{formatDateTime(n.occurredAt, i18n.resolvedLanguage)}</TableCell>
                <TableCell className="text-sm">
                  {n.channelName ?? <span className="text-muted-foreground italic">{t("notifications.history.deletedChannel")}</span>}
                </TableCell>
                <TableCell>
                  <div className="flex flex-col items-start gap-1">
                    <span className="text-sm">{isKnownKind(n.kind) ? t(`notifications.events.${n.kind}`) : n.kind}</span>
                    <Badge variant={severityVariant(n.severity)}>{t(`notifications.severity.${n.severity}`)}</Badge>
                  </div>
                </TableCell>
                <TableCell className="max-w-md min-w-64 text-sm break-words whitespace-normal">{n.summary}</TableCell>
                <TableCell className="text-sm">
                  <Badge variant={statusVariant(n.status)}>
                    {n.status === "Pending" && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                    {t(`notifications.status.${n.status}`)}
                  </Badge>
                  {n.finishedAt && (
                    <div className="text-muted-foreground mt-1 text-xs whitespace-nowrap">
                      {formatDateTime(n.finishedAt, i18n.resolvedLanguage)}
                    </div>
                  )}
                  {n.error && (
                    // The reason comes from the bot in English (e.g. missing permissions); it is not translated.
                    <p className="text-destructive mt-1 max-w-xs text-xs whitespace-normal" lang="en">
                      {n.error}
                    </p>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function HistoryLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();
  const { key, detail } = describeError(error);

  return (
    <ErrorText>
      {t("notifications.history.loadFailed")} {t(key)} {detail}
    </ErrorText>
  );
}
