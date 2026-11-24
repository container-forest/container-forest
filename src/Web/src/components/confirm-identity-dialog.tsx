import { useQuery } from "@tanstack/react-query";
import { type FormEvent, useState } from "react";
import { useTranslation } from "react-i18next";
import { useConfirmWithPasskeyMutation, useConfirmWithPasswordMutation } from "@/api/backup";
import { hasStatus } from "@/api/client";
import { meQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { getPasskey, isCancelled, passkeysSupported } from "@/features/passkeys/model";

/**
 * Asks for the password or a passkey before a sensitive change (ADR-0013). The aggregator answers with a short-lived
 * token, which goes to `onConfirmed`; the password leaves this component's state when the dialog closes.
 */
export function ConfirmIdentityDialog({
  open,
  description,
  onOpenChange,
  onConfirmed,
}: {
  open: boolean;
  /** Why this action asks for it. */
  description: string;
  onOpenChange: (open: boolean) => void;
  onConfirmed: (token: string) => void;
}) {
  const { t } = useTranslation();
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  // With two-factor on, a password alone confirms nothing: the code of the app goes with it (ADR-0019).
  const needsCode = useQuery(meQuery).data?.twoFactorEnabled === true;
  const withPassword = useConfirmWithPasswordMutation();
  const withPasskey = useConfirmWithPasskeyMutation();
  const pending = withPassword.isPending || withPasskey.isPending;
  const error = withPassword.error ?? (isCancelled(withPasskey.error) ? null : withPasskey.error);

  const change = (next: boolean) => {
    if (!next) {
      setPassword("");
      setCode("");
      withPassword.reset();
      withPasskey.reset();
    }

    onOpenChange(next);
  };

  const confirmed = (token: string) => {
    change(false);
    onConfirmed(token);
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    withPasskey.reset();
    withPassword.mutate({ password, code: needsCode ? code : undefined }, { onSuccess: confirmed });
  };

  return (
    <Dialog open={open} onOpenChange={change}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("backup.confirm.title")}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <form className="grid gap-4" noValidate onSubmit={submit}>
          <div className="grid gap-2">
            <Label htmlFor="confirm-password">{t("backup.confirm.password")}</Label>
            <Input
              id="confirm-password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </div>
          {needsCode && (
            <div className="grid gap-2">
              <Label htmlFor="confirm-code">{t("twoFactor.code")}</Label>
              <Input
                id="confirm-code"
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={7}
                value={code}
                onChange={(event) => setCode(event.target.value)}
              />
            </div>
          )}
          {error &&
            (hasStatus(error, 429) ? (
              <ErrorText>{t("login.errors.tooManyAttempts")}</ErrorText>
            ) : (
              <ErrorText>{t("backup.confirm.failed")}</ErrorText>
            ))}
          <DialogFooter>
            {passkeysSupported() && (
              <Button
                type="button"
                variant="outline"
                disabled={pending}
                onClick={() => {
                  withPassword.reset();
                  withPasskey.mutate((options) => getPasskey(options), { onSuccess: confirmed });
                }}
              >
                {withPasskey.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("backup.confirm.passkey")}
              </Button>
            )}
            <Button type="submit" disabled={pending || password.length === 0 || (needsCode && code.trim().length === 0)}>
              {withPassword.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("backup.confirm.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
