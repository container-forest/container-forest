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

interface SignInSettings {
  requireTwoFactorForAdministrators: boolean;
  requireTwoFactorAfterDiscord: boolean;
}

/**
 * How people sign in (ADR-0019): whether an administrator's password needs the app's code, and whether a Discord
 * sign-in counts as a password. A change asks for a fresh confirmation; a value set in the aggregator's environment is
 * shown locked.
 */
export function SignInSettingsCard() {
  const { t } = useTranslation();
  const settings = useQuery(signInSettingsQuery);
  const update = useUpdateSignInSettingsMutation();
  const [pending, setPending] = useState<SignInSettings>();
  const error = update.error ? describeError(update.error) : undefined;

  if (!settings.data) {
    return settings.isError ? <ErrorText>{t("signInSettings.loadFailed")}</ErrorText> : null;
  }

  const shown: SignInSettings = update.isPending && update.variables ? update.variables : settings.data;
  const change = (next: Partial<SignInSettings>) => {
    update.reset();
    setPending({
      requireTwoFactorForAdministrators: shown.requireTwoFactorForAdministrators,
      requireTwoFactorAfterDiscord: shown.requireTwoFactorAfterDiscord,
      ...next,
    });
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("signInSettings.title")}</CardTitle>
        <CardDescription>{t("signInSettings.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="grid gap-2">
          <div className="flex items-center gap-3">
            <Switch
              id="require-two-factor"
              checked={shown.requireTwoFactorForAdministrators}
              disabled={settings.data.locked || update.isPending}
              onCheckedChange={(next) => change({ requireTwoFactorForAdministrators: next })}
            />
            <Label htmlFor="require-two-factor">{t("signInSettings.requireLabel")}</Label>
          </div>
          <p className="text-muted-foreground text-sm">
            {settings.data.locked ? t("signInSettings.locked", { name: "CF_SIGNIN__REQUIRETWOFACTORFORADMINISTRATORS" }) : t("signInSettings.requireHint")}
          </p>
        </div>
        <div className="grid gap-2">
          <div className="flex items-center gap-3">
            <Switch
              id="require-two-factor-discord"
              checked={shown.requireTwoFactorAfterDiscord}
              disabled={settings.data.discordLocked || update.isPending}
              onCheckedChange={(next) => change({ requireTwoFactorAfterDiscord: next })}
            />
            <Label htmlFor="require-two-factor-discord">{t("signInSettings.discordLabel")}</Label>
          </div>
          <p className="text-muted-foreground text-sm">
            {settings.data.discordLocked ? t("signInSettings.locked", { name: "CF_SIGNIN__REQUIRETWOFACTORAFTERDISCORD" }) : t("signInSettings.discordHint")}
          </p>
        </div>
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
            update.mutate({ ...pending, confirmation });
          }
        }}
      />
    </Card>
  );
}
