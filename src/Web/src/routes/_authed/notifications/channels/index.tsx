import { useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { useDeleteChannelMutation, useUpdateChannelMutation } from "@/api/notifications";
import { notificationChannelsQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, Trash2Icon } from "@/components/icons";
import { CreateChannelDialog, EditChannelDialog } from "@/components/notifications/channel-dialog";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { type NotificationChannel, describeError } from "@/features/notifications/model";
import { formatDate } from "@/lib/format";

export const Route = createFileRoute("/_authed/notifications/channels/")({
  loader: ({ context }) => context.queryClient.ensureQueryData(notificationChannelsQuery),
  component: ChannelsScreen,
  errorComponent: ChannelsLoadError,
});

/** Discord channels the bot posts notifications to; administrators only. */
function ChannelsScreen() {
  const { t } = useTranslation();
  const { data: channels } = useSuspenseQuery(notificationChannelsQuery);

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{t("notifications.channels.title")}</h1>
          <p className="text-muted-foreground mt-1 max-w-2xl">{t("notifications.channels.description")}</p>
        </div>
        <CreateChannelDialog />
      </div>

      {channels.length === 0 ? (
        <p className="text-muted-foreground">{t("notifications.channels.empty")}</p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("notifications.channels.columns.name")}</TableHead>
              <TableHead>{t("notifications.channels.columns.discordId")}</TableHead>
              <TableHead>{t("notifications.channels.columns.created")}</TableHead>
              <TableHead>{t("notifications.channels.columns.enabled")}</TableHead>
              <TableHead className="text-right">{t("notifications.channels.columns.actions")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {channels.map((channel) => (
              <ChannelRow key={channel.id} channel={channel} />
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function ChannelRow({ channel }: { channel: NotificationChannel }) {
  const { t, i18n } = useTranslation();
  const update = useUpdateChannelMutation(channel.id);
  const remove = useDeleteChannelMutation(channel.id);
  const error = update.error ?? remove.error;
  const described = error ? describeError(error) : undefined;
  // While the switch's request is on its way it shows the requested state, so a click does not seem ignored.
  const enabled = update.isPending ? (update.variables?.isEnabled ?? channel.isEnabled) : channel.isEnabled;

  return (
    <TableRow>
      <TableCell>
        <Link
          to="/notifications/channels/$channelId"
          params={{ channelId: channel.id }}
          className="font-medium hover:underline"
        >
          {channel.name}
        </Link>
        {described && (
          <ErrorText>
            {t(described.key)} {described.detail}
          </ErrorText>
        )}
      </TableCell>
      <TableCell>
        <span className="flex items-center gap-1">
          <code className="font-mono text-sm">{channel.discordChannelId}</code>
          <CopyButton text={channel.discordChannelId} label={t("notifications.channels.copyId")} variant="ghost" />
        </span>
      </TableCell>
      <TableCell className="text-muted-foreground text-sm">{formatDate(channel.createdAt, i18n.resolvedLanguage)}</TableCell>
      <TableCell>
        <Switch
          checked={enabled}
          disabled={update.isPending}
          aria-label={t("notifications.channels.toggle", { name: channel.name })}
          onCheckedChange={(isEnabled) =>
            update.mutate({ name: channel.name, discordChannelId: channel.discordChannelId, isEnabled })
          }
        />
      </TableCell>
      <TableCell>
        <div className="flex justify-end gap-1">
          <EditChannelDialog channel={channel} />
          <ConfirmAction
            variant="ghost"
            size="sm"
            title={t("notifications.channels.delete.title", { name: channel.name })}
            description={t("notifications.channels.delete.description")}
            confirmLabel={t("notifications.channels.delete.confirm")}
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

function ChannelsLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();
  const { key, detail } = describeError(error);

  return (
    <ErrorText>
      {t("notifications.channels.loadFailed")} {t(key)} {detail}
    </ErrorText>
  );
}
