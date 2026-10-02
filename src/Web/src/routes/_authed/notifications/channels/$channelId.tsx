import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { useDeleteRuleMutation, useUpdateRuleMutation } from "@/api/notifications";
import { notificationChannelsQuery, notificationRulesQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { ArrowLeftIcon, Loader2Icon, Trash2Icon } from "@/components/icons";
import { CreateRuleDialog, EditRuleEventsDialog } from "@/components/notifications/rule-dialogs";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { type NotificationRule, describeError, isKnownKind, ruleScope } from "@/features/notifications/model";
import { formatDate } from "@/lib/format";

export const Route = createFileRoute("/_authed/notifications/channels/$channelId")({
  loader: ({ context, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(notificationChannelsQuery),
      context.queryClient.ensureQueryData(notificationRulesQuery(params.channelId)),
    ]),
  component: ChannelRulesScreen,
  errorComponent: RulesLoadError,
});

/** One channel's rules: which events reach it, and from where. */
function ChannelRulesScreen() {
  const { t } = useTranslation();
  const { channelId } = Route.useParams();
  // The list may be refetched after a delete elsewhere; the channel then simply is gone.
  const { data: channels } = useQuery(notificationChannelsQuery);
  const { data: rules } = useSuspenseQuery(notificationRulesQuery(channelId));
  const channel = channels?.find((c) => c.id === channelId);

  return (
    <div className="grid gap-6">
      <div>
        <Link
          to="/notifications/channels"
          className="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm"
        >
          <ArrowLeftIcon />
          {t("notifications.rules.back")}
        </Link>
      </div>

      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="font-heading flex items-center gap-2 text-2xl font-semibold">
            {t("notifications.rules.title", { name: channel?.name ?? "…" })}
            {channel && !channel.isEnabled && <Badge variant="outline">{t("notifications.channels.disabled")}</Badge>}
          </h1>
          <p className="text-muted-foreground mt-1 max-w-2xl">{t("notifications.rules.description")}</p>
          {channel && !channel.isEnabled && (
            <p className="text-muted-foreground mt-1 text-sm">{t("notifications.rules.channelDisabled")}</p>
          )}
        </div>
        <CreateRuleDialog channelId={channelId} />
      </div>

      {rules.length === 0 ? (
        <p className="text-muted-foreground">{t("notifications.rules.empty")}</p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("notifications.rules.columns.scope")}</TableHead>
              <TableHead>{t("notifications.rules.columns.events")}</TableHead>
              <TableHead>{t("notifications.rules.columns.created")}</TableHead>
              <TableHead>{t("notifications.rules.columns.enabled")}</TableHead>
              <TableHead className="text-right">{t("notifications.rules.columns.actions")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rules.map((rule) => (
              <RuleRow key={rule.id} rule={rule} />
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function RuleRow({ rule }: { rule: NotificationRule }) {
  const { t, i18n } = useTranslation();
  const update = useUpdateRuleMutation(rule.channelId, rule.id);
  const remove = useDeleteRuleMutation(rule.channelId, rule.id);
  const error = update.error ?? remove.error;
  const described = error ? describeError(error) : undefined;
  const enabled = update.isPending ? (update.variables?.isEnabled ?? rule.isEnabled) : rule.isEnabled;

  return (
    <TableRow>
      <TableCell>
        <RuleScopeText rule={rule} />
        {described && (
          <ErrorText>
            {t(described.key)} {described.detail}
          </ErrorText>
        )}
      </TableCell>
      <TableCell>
        <div className="flex max-w-md flex-wrap gap-1">
          {rule.events.map((kind) => (
            <Badge key={kind} variant="secondary">
              {isKnownKind(kind) ? t(`notifications.events.${kind}`) : kind}
            </Badge>
          ))}
        </div>
      </TableCell>
      <TableCell className="text-muted-foreground text-sm">{formatDate(rule.createdAt, i18n.resolvedLanguage)}</TableCell>
      <TableCell>
        <Switch
          checked={enabled}
          disabled={update.isPending}
          aria-label={t("notifications.rules.toggle")}
          onCheckedChange={(isEnabled) => update.mutate({ events: rule.events, isEnabled })}
        />
      </TableCell>
      <TableCell>
        <div className="flex justify-end gap-1">
          <EditRuleEventsDialog rule={rule} />
          <ConfirmAction
            variant="ghost"
            size="sm"
            title={t("notifications.rules.delete.title")}
            description={t("notifications.rules.delete.description")}
            confirmLabel={t("notifications.rules.delete.confirm")}
            disabled={remove.isPending}
            onConfirm={() => remove.mutate()}
          >
            {remove.isPending ? (
              <Loader2Icon data-icon="inline-start" className="animate-spin" />
            ) : (
              <Trash2Icon data-icon="inline-start" />
            )}
            {t("notifications.delete")}
          </ConfirmAction>
        </div>
      </TableCell>
    </TableRow>
  );
}

function RuleScopeText({ rule }: { rule: NotificationRule }) {
  const { t } = useTranslation();
  const scope = ruleScope(rule);
  const gone = t("notifications.rules.scope.deleted");

  switch (scope.kind) {
    case "system":
      return <span className="font-medium">{t("notifications.rules.scope.system")}</span>;
    case "environment":
      return (
        <span>
          <span className="text-muted-foreground">{t("notifications.rules.scope.environment")}</span>{" "}
          <span className="font-medium">{scope.environment ?? gone}</span>
        </span>
      );
    case "stack":
      return (
        <span>
          <span className="text-muted-foreground">{t("notifications.rules.scope.stack")}</span>{" "}
          <span className="font-medium">{scope.stack ?? gone}</span>
          {scope.environment && <span className="text-muted-foreground"> · {scope.environment}</span>}
        </span>
      );
  }
}

function RulesLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();
  const { key, detail } = describeError(error);

  return (
    <div className="grid gap-3">
      <ErrorText>
        {t("notifications.rules.loadFailed")} {t(key)} {detail}
      </ErrorText>
      <div>
        <Link to="/notifications/channels" className="text-sm hover:underline">
          {t("notifications.rules.back")}
        </Link>
      </div>
    </div>
  );
}
