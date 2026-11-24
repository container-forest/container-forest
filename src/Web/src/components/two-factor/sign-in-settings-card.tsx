import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { signInSettingsQuery } from "@/api/queries";
import { useUpdateSignInSettingsMutation } from "@/api/two-factor";
import { ConfirmIdentityDialog } from "@/components/confirm-identity-dialog";
import { ErrorText } from "@/components/form-messages";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { describeError } from "@/features/stacks/model";

/**
 * Whether administrators must have two-factor authentication or a passkey (ADR-0019). The change asks for a fresh
 * confirmation; a value set in the aggregator's environment is shown locked.
 */
export function SignInSettingsCard() {
  const { t } = useTranslation();
  const settings = useQuery(signInSettingsQuery);
  const update = useUpdateSignInSettingsMutation();
  const [pending, setPending] = useState<boolean>();
  const error = update.error ? describeError(update.error) : undefined;

  if (!settings.data) {
    return settings.isError ? <ErrorText>{t("signInSettings.loadFailed")}</ErrorText> : null;
  }

  const checked = update.isPending && update.variables ? update.variables.requireTwoFactorForAdministrators : settings.data.requireTwoFactorForAdministrators;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("signInSettings.title")}</CardTitle>
        <CardDescription>{t("signInSettings.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-2">
        <div className="flex items-center gap-3">
          <Switch
            id="require-two-factor"
            checked={checked}
            disabled={settings.data.locked || update.isPending}
            onCheckedChange={(next) => {
              update.reset();
              setPending(next);
            }}
          />
          <Label htmlFor="require-two-factor">{t("signInSettings.requireLabel")}</Label>
        </div>
        <p className="text-muted-foreground text-sm">
          {settings.data.locked ? t("signInSettings.locked") : t("signInSettings.requireHint")}
        </p>
        <div aria-live="polite">
          {error && (
            <ErrorText>
              {t("signInSettings.failed")} {t(error.key)} {error.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
      <ConfirmIdentityDialog
        open={pending !== undefined}
        description={t("signInSettings.confirm")}
        onOpenChange={(open) => {
          if (!open) {
            setPending(undefined);
          }
        }}
        onConfirmed={(confirmation) => {
          if (pending !== undefined) {
            update.mutate({ requireTwoFactorForAdministrators: pending, confirmation });
          }
        }}
      />
    </Card>
  );
}
