import { useQuery } from "@tanstack/react-query";
import { type FormEvent, useState } from "react";
import { useTranslation } from "react-i18next";
import { meQuery, twoFactorQuery } from "@/api/queries";
import {
  useBeginTwoFactorSetupMutation,
  useDisableTwoFactorMutation,
  useEnableTwoFactorMutation,
  useRegenerateRecoveryCodesMutation,
} from "@/api/two-factor";
import { ConfirmIdentityDialog } from "@/components/confirm-identity-dialog";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { DownloadIcon, Loader2Icon, ShieldPlusIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { describeError } from "@/features/stacks/model";
import { groupKey, isTwoFactorCode, normalizeCode, qrModules, recoveryCodesFile } from "@/features/two-factor/model";

type Confirming = "setup" | "codes" | "disable";

/**
 * Two-factor authentication on the account screen (ADR-0019): set up an authenticator app from a QR code, see how
 * many recovery codes are left, make new ones, turn it off. Every step but the code itself needs a fresh confirmation.
 * The key and the recovery codes live only in this component's state while they are shown.
 */
export function TwoFactorCard() {
  const { t } = useTranslation();
  const status = useQuery(twoFactorQuery);
  const me = useQuery(meQuery);
  const begin = useBeginTwoFactorSetupMutation();
  const enable = useEnableTwoFactorMutation();
  const regenerate = useRegenerateRecoveryCodesMutation();
  const disable = useDisableTwoFactorMutation();
  const [confirming, setConfirming] = useState<Confirming>();
  const [setup, setSetup] = useState<{ sharedKey: string; uri: string }>();
  const [code, setCode] = useState("");
  const [codes, setCodes] = useState<readonly string[]>();

  const failure = begin.error ?? enable.error ?? regenerate.error ?? disable.error;
  const error = failure ? describeError(failure) : undefined;

  const onConfirmed = (token: string) => {
    if (confirming === "setup") {
      begin.mutate(token, { onSuccess: (started) => setSetup(started) });
    } else if (confirming === "codes") {
      regenerate.mutate(token, { onSuccess: (result) => setCodes(result.codes) });
    } else if (confirming === "disable") {
      disable.mutate(token);
    }
  };

  const onEnable = (event: FormEvent) => {
    event.preventDefault();
    enable.mutate(normalizeCode(code), {
      onSuccess: (result) => {
        setSetup(undefined);
        setCode("");
        setCodes(result.codes);
      },
    });
  };

  const reset = () => [begin, enable, regenerate, disable].forEach((mutation) => mutation.reset());

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("twoFactor.title")}</CardTitle>
        <CardDescription>{t("twoFactor.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {me.data?.twoFactorSetupRequired && (
          <p role="alert" className="rounded-md border border-amber-500/50 p-3 text-sm">
            {t("twoFactor.requiredNotice")}
          </p>
        )}

        {status.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("twoFactor.loading")}
          </p>
        ) : status.isError ? (
          <ErrorText>{t("twoFactor.loadFailed")}</ErrorText>
        ) : codes ? (
          <RecoveryCodes codes={codes} userName={me.data?.userName ?? ""} onDone={() => setCodes(undefined)} />
        ) : setup ? (
          <form className="grid gap-4" noValidate onSubmit={onEnable}>
            <p className="text-sm">{t("twoFactor.setup.scan")}</p>
            <QrCode text={setup.uri} label={t("twoFactor.setup.qrLabel")} />
            <div className="grid gap-1">
              <span className="text-muted-foreground text-sm">{t("twoFactor.setup.manual")}</span>
              <div className="flex flex-wrap items-center gap-2">
                <code className="bg-muted rounded px-2 py-1 font-mono text-sm break-all">{groupKey(setup.sharedKey)}</code>
                <CopyButton text={setup.sharedKey} label={t("twoFactor.setup.copyKey")} />
              </div>
            </div>
            <div className="grid max-w-xs gap-2">
              <Label htmlFor="two-factor-setup-code">{t("twoFactor.setup.code")}</Label>
              <Input
                id="two-factor-setup-code"
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={7}
                value={code}
                onChange={(event) => {
                  setCode(event.target.value);
                  enable.reset();
                }}
              />
            </div>
            <div className="flex flex-wrap gap-2">
              <Button type="submit" disabled={enable.isPending || !isTwoFactorCode(code)}>
                {enable.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("twoFactor.setup.enable")}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={() => {
                  setSetup(undefined);
                  setCode("");
                  reset();
                }}
              >
                {t("twoFactor.setup.cancel")}
              </Button>
            </div>
          </form>
        ) : status.data.enabled ? (
          <div className="grid gap-3">
            <p className="text-sm">{t("twoFactor.on", { count: status.data.recoveryCodesLeft })}</p>
            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                variant="outline"
                disabled={regenerate.isPending}
                onClick={() => {
                  reset();
                  setConfirming("codes");
                }}
              >
                {regenerate.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("twoFactor.newCodes")}
              </Button>
              <Button
                type="button"
                variant="outline"
                disabled={disable.isPending || status.data.required}
                onClick={() => {
                  reset();
                  setConfirming("disable");
                }}
              >
                {disable.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("twoFactor.disable")}
              </Button>
            </div>
            {status.data.required && <p className="text-muted-foreground text-sm">{t("twoFactor.requiredHint")}</p>}
          </div>
        ) : (
          <div className="grid gap-3">
            <p className="text-muted-foreground text-sm">{t("twoFactor.off")}</p>
            <div>
              <Button
                type="button"
                disabled={begin.isPending}
                onClick={() => {
                  reset();
                  setConfirming("setup");
                }}
              >
                {begin.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <ShieldPlusIcon data-icon="inline-start" />}
                {t("twoFactor.setUp")}
              </Button>
            </div>
          </div>
        )}

        <div aria-live="polite">
          {error && (
            <ErrorText>
              {t("twoFactor.failed")} {t(error.key)} {error.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
      <ConfirmIdentityDialog
        open={confirming !== undefined}
        description={t(confirming === "disable" ? "twoFactor.confirmDisable" : "twoFactor.confirm")}
        onOpenChange={(open) => {
          if (!open) {
            setConfirming(undefined);
          }
        }}
        onConfirmed={onConfirmed}
      />
    </Card>
  );
}

/** The otpauth:// link as an SVG drawn from the code's modules, dark on white whatever the theme, as scanners expect. */
function QrCode({ text, label }: { text: string; label: string }) {
  const modules = qrModules(text);
  const size = modules.length;

  return (
    <svg role="img" aria-label={label} viewBox={`0 0 ${size} ${size}`} className="size-48 rounded bg-white" shapeRendering="crispEdges">
      {modules.flatMap((row, y) => row.map((dark, x) => (dark ? <rect key={`${x}-${y}`} x={x} y={y} width={1} height={1} fill="#000" /> : null)))}
    </svg>
  );
}

/** Recovery codes, shown once: copy them, or save them as a file, before going on. */
function RecoveryCodes({ codes, userName, onDone }: { codes: readonly string[]; userName: string; onDone: () => void }) {
  const { t } = useTranslation();
  const text = recoveryCodesFile(userName, codes);

  const download = () => {
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "container-forest-recovery-codes.txt";
    link.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="grid gap-3">
      <p className="text-sm">{t("twoFactor.codes.description")}</p>
      <ul className="bg-muted grid grid-cols-2 gap-x-6 gap-y-1 rounded-md p-3 font-mono text-sm sm:max-w-sm">
        {codes.map((code) => (
          <li key={code}>{code}</li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-2">
        <CopyButton text={codes.join("\n")} label={t("twoFactor.codes.copy")} />
        <Button type="button" variant="outline" onClick={download}>
          <DownloadIcon data-icon="inline-start" />
          {t("twoFactor.codes.download")}
        </Button>
        <Button type="button" onClick={onDone}>
          {t("twoFactor.codes.done")}
        </Button>
      </div>
    </div>
  );
}
