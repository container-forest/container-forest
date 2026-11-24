import { type FormEvent, useState } from "react";
import { useTranslation } from "react-i18next";
import { useTwoFactorLoginMutation } from "@/api/auth";
import { hasStatus, isUnauthorized } from "@/api/client";
import type { Me } from "@/api/auth";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isTwoFactorCode, normalizeCode } from "@/features/two-factor/model";

/**
 * The second step of a password sign-in (ADR-0019): the code of the authenticator app, or a recovery code when the
 * phone is not at hand. The aggregator remembers the password step for five minutes.
 */
export function TwoFactorLoginStep({ onSignedIn, onBack }: { onSignedIn: (me: Me) => void; onBack: () => void }) {
  const { t } = useTranslation();
  const [code, setCode] = useState("");
  const [recovery, setRecovery] = useState(false);
  const login = useTwoFactorLoginMutation();
  const valid = recovery ? code.trim().length > 0 : isTwoFactorCode(code);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    login.mutate(recovery ? code.trim() : normalizeCode(code), { onSuccess: onSignedIn });
  };

  return (
    <form className="grid gap-4" noValidate onSubmit={submit}>
      <p className="text-muted-foreground text-sm">{recovery ? t("twoFactor.login.recoveryHint") : t("twoFactor.login.hint")}</p>
      <div className="grid gap-2">
        <Label htmlFor="two-factor-code">{recovery ? t("twoFactor.recoveryCode") : t("twoFactor.code")}</Label>
        <Input
          id="two-factor-code"
          key={recovery ? "recovery" : "app"}
          autoFocus
          inputMode={recovery ? "text" : "numeric"}
          autoComplete={recovery ? "off" : "one-time-code"}
          spellCheck={false}
          maxLength={recovery ? 32 : 7}
          value={code}
          onChange={(event) => {
            setCode(event.target.value);
            login.reset();
          }}
        />
      </div>

      {login.isError && (
        <p role="alert" className="text-destructive text-sm">
          {isUnauthorized(login.error)
            ? t("twoFactor.login.refused")
            : hasStatus(login.error, 429)
              ? t("login.errors.tooManyAttempts")
              : t("login.errors.failed")}
        </p>
      )}

      <Button type="submit" className="w-full" disabled={login.isPending || !valid}>
        {login.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
        {t("twoFactor.login.submit")}
      </Button>
      <div className="flex flex-wrap justify-between gap-2">
        <Button type="button" variant="link" className="h-auto p-0" onClick={onBack}>
          {t("twoFactor.login.back")}
        </Button>
        <Button
          type="button"
          variant="link"
          className="h-auto p-0"
          onClick={() => {
            setRecovery(!recovery);
            setCode("");
            login.reset();
          }}
        >
          {recovery ? t("twoFactor.login.useApp") : t("twoFactor.login.useRecovery")}
        </Button>
      </div>
    </form>
  );
}
