import { type ChangeEvent, useState } from "react";
import { useTranslation } from "react-i18next";
import { CopyButton } from "@/components/copy-button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { type AgentSettings, agentComposeFile, defaultAgentSettings, isControllerUrl } from "@/features/environments/model";
import { formatDateTime } from "@/lib/format";

/**
 * A freshly issued enrollment token and the agent's compose file with it. The token arrives here as a prop from
 * the answer that issued it and lives only as long as this component: the aggregator keeps just its hash, so it
 * is shown once. The settings around it (address, host name, image) are not secret and can be edited.
 */
export function EnrollmentInstructions({
  environmentName,
  token,
  expiresAt,
}: {
  environmentName: string;
  token: string;
  expiresAt: string;
}) {
  const { t, i18n } = useTranslation();
  const [settings, setSettings] = useState<AgentSettings>(() => defaultAgentSettings(environmentName, window.location.origin));
  const compose = agentComposeFile(settings, token);
  const urlValid = isControllerUrl(settings.controllerUrl);

  const field = (key: keyof AgentSettings) => ({
    value: settings[key],
    onChange: (event: ChangeEvent<HTMLInputElement>) => setSettings({ ...settings, [key]: event.target.value }),
  });

  return (
    <div className="grid gap-4">
      <div role="status" className="bg-muted/50 rounded-lg border p-3 text-sm">
        <p className="font-medium">{t("enrollment.tokenOnce")}</p>
        <p className="text-muted-foreground mt-1">
          {t("enrollment.tokenExpires", { time: formatDateTime(expiresAt, i18n.resolvedLanguage) })}
        </p>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="enrollment-token">{t("enrollment.token")}</Label>
        <div className="flex gap-2">
          <Input id="enrollment-token" readOnly value={token} className="font-mono" onFocus={(event) => event.target.select()} />
          <CopyButton text={token} label={t("enrollment.copyToken")} />
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="grid gap-1.5 sm:col-span-2">
          <Label htmlFor="agent-controller-url">{t("enrollment.controllerUrl")}</Label>
          <Input
            id="agent-controller-url"
            inputMode="url"
            autoComplete="off"
            aria-invalid={urlValid ? undefined : true}
            aria-describedby="agent-controller-url-hint"
            {...field("controllerUrl")}
          />
          <p id="agent-controller-url-hint" className={urlValid ? "text-muted-foreground text-sm" : "text-destructive text-sm"}>
            {urlValid ? t("enrollment.controllerUrlHint") : t("enrollment.controllerUrlInvalid")}
          </p>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="agent-hostname">{t("enrollment.hostname")}</Label>
          <Input id="agent-hostname" autoComplete="off" {...field("hostname")} />
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="agent-image">{t("enrollment.image")}</Label>
          <Input id="agent-image" autoComplete="off" className="font-mono text-xs" {...field("image")} />
        </div>
      </div>

      <div className="grid gap-1.5">
        <div className="flex items-end justify-between gap-2">
          <Label htmlFor="agent-compose">{t("enrollment.compose")}</Label>
          <CopyButton text={compose} label={t("enrollment.copyCompose")} />
        </div>
        <pre id="agent-compose" tabIndex={0} className="bg-muted/40 overflow-x-auto rounded-lg border p-3 font-mono text-xs">
          {compose}
        </pre>
        <p className="text-muted-foreground text-sm">{t("enrollment.composeHint")}</p>
      </div>
    </div>
  );
}
