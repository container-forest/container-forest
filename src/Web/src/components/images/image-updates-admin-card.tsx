import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useRotateSigningKeyMutation, useUpdateImageUpdateSettingsMutation } from "@/api/images";
import { aggregatorSigningKeysQuery, imageUpdateSettingsQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { describeError } from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";

/**
 * Image updates for administrators (ADR-0015): the aggregator's signing key, to add to the git servers' service users
 * so update commits show as verified, and polling of registries.
 */
export function ImageUpdatesAdminCard() {
  const { t, i18n } = useTranslation();
  const keys = useQuery(aggregatorSigningKeysQuery);
  const settings = useQuery(imageUpdateSettingsQuery);
  const rotate = useRotateSigningKeyMutation();
  const update = useUpdateImageUpdateSettingsMutation();
  const [interval, setInterval] = useState<string>();
  const active = keys.data?.find((k) => k.retiredAt === null || k.retiredAt === undefined);
  const retired = keys.data?.filter((k) => k !== active) ?? [];
  const error = rotate.error ?? update.error;
  const described = error ? describeError(error) : undefined;
  const minutes = interval ?? String(settings.data?.pollingIntervalMinutes ?? 5);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("images.admin.title")}</CardTitle>
        <CardDescription>{t("images.admin.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="grid gap-2">
          <h3 className="text-sm font-medium">{t("images.admin.key")}</h3>
          {active ? (
            <>
              <code className="font-mono text-xs break-all">{active.publicKey}</code>
              <p className="text-muted-foreground text-xs">
                {active.fingerprint} · {formatDateTime(active.createdAt, i18n.resolvedLanguage)}
              </p>
              <div className="flex flex-wrap gap-2">
                <CopyButton text={active.publicKey} label={t("images.admin.copyKey")} />
                <ConfirmAction
                  title={t("images.admin.rotateTitle")}
                  description={t("images.admin.rotateDescription")}
                  confirmLabel={t("images.admin.rotate")}
                  disabled={rotate.isPending}
                  onConfirm={() => rotate.mutate()}
                >
                  {t("images.admin.rotate")}
                </ConfirmAction>
              </div>
            </>
          ) : (
            <p className="text-muted-foreground text-sm">{t("images.admin.noKey")}</p>
          )}
          {retired.length > 0 && (
            <details className="text-sm">
              <summary className="cursor-pointer">{t("images.admin.retired", { count: retired.length })}</summary>
              <ul className="mt-2 grid gap-1">
                {retired.map((key) => (
                  <li key={key.id} className="font-mono text-xs break-all">
                    {key.publicKey}
                  </li>
                ))}
              </ul>
            </details>
          )}
        </div>

        {settings.data && (
          <form
            className="grid gap-3 border-t pt-4"
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              update.mutate(
                { pollingEnabled: settings.data.pollingEnabled, pollingIntervalMinutes: Number(minutes) },
                { onSuccess: () => setInterval(undefined) },
              );
            }}
          >
            <div className="flex items-center gap-3">
              <Switch
                id="image-polling"
                checked={update.isPending && update.variables ? update.variables.pollingEnabled : settings.data.pollingEnabled}
                disabled={update.isPending || settings.data.pollingEnabledLocked}
                onCheckedChange={(enabled) =>
                  update.mutate({ pollingEnabled: enabled, pollingIntervalMinutes: settings.data.pollingIntervalMinutes })
                }
              />
              <Label htmlFor="image-polling">{t("images.admin.polling")}</Label>
            </div>
            <div className="flex flex-wrap items-end gap-2">
              <div className="grid w-40 gap-1.5">
                <Label htmlFor="image-polling-interval">{t("images.admin.interval")}</Label>
                <Input
                  id="image-polling-interval"
                  type="number"
                  min={1}
                  max={1440}
                  value={minutes}
                  disabled={settings.data.pollingIntervalLocked}
                  onChange={(event) => setInterval(event.target.value)}
                />
              </div>
              <Button type="submit" variant="outline" disabled={update.isPending || settings.data.pollingIntervalLocked || interval === undefined}>
                {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("images.admin.save")}
              </Button>
            </div>
            {(settings.data.pollingEnabledLocked || settings.data.pollingIntervalLocked) && (
              <p className="text-muted-foreground text-sm">{t("images.admin.locked")}</p>
            )}
          </form>
        )}

        <div aria-live="polite">
          {described && (
            <ErrorText>
              {t(described.key)} {described.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
