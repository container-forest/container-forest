import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useUpdateBackupSettingsMutation } from "@/api/backup";
import { hasStatus } from "@/api/client";
import { ConfirmIdentityDialog } from "@/components/confirm-identity-dialog";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { type BackupForm, type BackupSettings, backupFormOf, backupFormSchema, toUpdateRequest } from "@/features/backup/model";
import { describeError } from "@/features/stacks/model";
import type { MessageKey } from "@/i18n";

type TextField = "intervalHours" | "recipient" | "endpoint" | "bucket" | "region" | "prefix" | "accessKeyId";

/** What a field is called, and what it needs in order to be filled in. */
const textFields: readonly { name: TextField; label: MessageKey; hint: MessageKey; mono?: boolean }[] = [
  { name: "intervalHours", label: "backup.form.intervalHours", hint: "backup.form.intervalHoursHint" },
  { name: "recipient", label: "backup.form.recipient", hint: "backup.form.recipientHint", mono: true },
  { name: "endpoint", label: "backup.form.endpoint", hint: "backup.form.endpointHint", mono: true },
  { name: "bucket", label: "backup.form.bucket", hint: "backup.form.bucketHint", mono: true },
  { name: "region", label: "backup.form.region", hint: "backup.form.regionHint", mono: true },
  { name: "prefix", label: "backup.form.prefix", hint: "backup.form.prefixHint", mono: true },
  { name: "accessKeyId", label: "backup.form.accessKeyId", hint: "backup.form.accessKeyIdHint", mono: true },
];

/**
 * The backup settings. A field set in the aggregator's environment is shown but cannot be edited. Saving asks for a
 * fresh password or passkey first, because these settings decide where a copy of every secret goes.
 */
export function BackupSettingsForm({ settings }: { settings: BackupSettings }) {
  const { t } = useTranslation();
  const update = useUpdateBackupSettingsMutation();
  const [confirming, setConfirming] = useState<BackupForm>();
  const form = useForm<BackupForm>({ resolver: zodResolver(backupFormSchema), values: backupFormOf(settings) });
  const errors = form.formState.errors;
  const saveError = update.error ? describeError(update.error) : undefined;
  const secret = settings.secretAccessKey;
  const removeSecret = form.watch("removeSecret");

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("backup.form.title")}</CardTitle>
        <CardDescription>{t("backup.form.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="grid max-w-xl gap-4"
          noValidate
          onSubmit={form.handleSubmit((values) => {
            update.reset();
            setConfirming(values);
          })}
        >
          <div className="flex items-center gap-3">
            <Controller
              control={form.control}
              name="enabled"
              render={({ field }) => (
                <Switch
                  id="backup-enabled"
                  checked={field.value}
                  disabled={settings.enabled.locked}
                  onCheckedChange={field.onChange}
                />
              )}
            />
            <Label htmlFor="backup-enabled">{t("backup.form.enabled")}</Label>
            {settings.enabled.locked && <Badge variant="secondary">{t("backup.form.fromEnvironment")}</Badge>}
          </div>

          {textFields.map(({ name, label, hint, mono }) => {
            const setting = settings[name];
            return (
              <div key={name} className="grid gap-2">
                <div className="flex items-center gap-2">
                  <Label htmlFor={`backup-${name}`}>{t(label)}</Label>
                  {setting.locked && <Badge variant="secondary">{t("backup.form.fromEnvironment")}</Badge>}
                </div>
                <Input
                  id={`backup-${name}`}
                  autoComplete="off"
                  spellCheck={false}
                  className={mono ? "font-mono" : undefined}
                  readOnly={setting.locked}
                  aria-invalid={errors[name] ? true : undefined}
                  {...form.register(name)}
                />
                <p className="text-muted-foreground text-sm">{t(hint)}</p>
                <FieldError id={`backup-${name}-error`} message={errors[name]?.message} />
                {setting.problem && <ErrorText>{setting.problem}</ErrorText>}
              </div>
            );
          })}

          <div className="grid gap-2">
            <div className="flex items-center gap-2">
              <Label htmlFor="backup-secretAccessKey">{t("backup.form.secretAccessKey")}</Label>
              {secret.locked && <Badge variant="secondary">{t("backup.form.fromEnvironment")}</Badge>}
            </div>
            <Input
              id="backup-secretAccessKey"
              type="password"
              autoComplete="new-password"
              className="font-mono"
              readOnly={secret.locked}
              disabled={removeSecret}
              placeholder={secret.isSet ? t("backup.form.secretKept") : undefined}
              {...form.register("secretAccessKey")}
            />
            <p className="text-muted-foreground text-sm">{t("backup.form.secretHint")}</p>
            {secret.problem && <ErrorText>{secret.problem}</ErrorText>}
            {secret.isSet && !secret.locked && (
              <div className="flex items-center gap-2">
                <Controller
                  control={form.control}
                  name="removeSecret"
                  render={({ field }) => (
                    <Checkbox id="backup-removeSecret" checked={field.value} onCheckedChange={(checked) => field.onChange(checked === true)} />
                  )}
                />
                <Label htmlFor="backup-removeSecret">{t("backup.form.removeSecret")}</Label>
              </div>
            )}
          </div>

          {saveError && (
            <ErrorText>
              {hasStatus(update.error, 403) ? t("backup.form.confirmationExpired") : t(saveError.key)} {saveError.detail}
            </ErrorText>
          )}
          {update.isSuccess && (
            <p role="status" className="text-sm">
              {t("backup.form.saved")}
            </p>
          )}

          <div>
            <Button type="submit" disabled={update.isPending}>
              {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("backup.form.save")}
            </Button>
          </div>
        </form>

        <ConfirmIdentityDialog
          open={confirming !== undefined}
          description={t("backup.confirm.description")}
          onOpenChange={(open) => {
            if (!open) {
              setConfirming(undefined);
            }
          }}
          onConfirmed={(token) => {
            if (confirming) {
              // The answer has the saved values, and an empty secret field: what was typed must not stay on screen.
              update.mutate(toUpdateRequest(confirming, token), { onSuccess: (saved) => form.reset(backupFormOf(saved)) });
            }
          }}
        />
      </CardContent>
    </Card>
  );
}
