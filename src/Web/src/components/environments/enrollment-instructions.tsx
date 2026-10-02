import { type ChangeEvent, useState } from "react";
import { useTranslation } from "react-i18next";
import { CopyButton } from "@/components/copy-button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  AGENT_COMPOSE_FILE,
  type AgentSettings,
  agentEnvFile,
  defaultAgentSettings,
  isDomain,
  isHostname,
} from "@/features/environments/model";
import { formatDateTime } from "@/lib/format";

/**
 * A freshly issued enrollment token and the two files that start the agent with it: compose.yaml, the same as
 * deploy/agent/compose.yaml, and the .env file next to it, which holds the token. The token arrives here as a prop
 * from the answer that issued it and lives only as long as this component: the aggregator keeps just its hash, so
 * it is shown once. Domain and host name are not secret and can be edited.
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
  const [settings, setSettings] = useState<AgentSettings>(() => defaultAgentSettings(environmentName, window.location.host));
  const env = agentEnvFile(settings, token);
  const domainValid = isDomain(settings.domain);
  const hostnameValid = isHostname(settings.hostname);

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
        <div className="grid gap-1.5">
          <Label htmlFor="agent-domain">{t("enrollment.domain")}</Label>
          <Input
            id="agent-domain"
            autoComplete="off"
            aria-invalid={domainValid ? undefined : true}
            aria-describedby="agent-domain-hint"
            {...field("domain")}
          />
          <p id="agent-domain-hint" className={domainValid ? "text-muted-foreground text-sm" : "text-destructive text-sm"}>
            {domainValid ? t("enrollment.domainHint") : t("enrollment.domainInvalid")}
          </p>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="agent-hostname">{t("enrollment.hostname")}</Label>
          <Input
            id="agent-hostname"
            autoComplete="off"
            aria-invalid={hostnameValid ? undefined : true}
            aria-describedby={hostnameValid ? undefined : "agent-hostname-error"}
            {...field("hostname")}
          />
          {!hostnameValid && (
            <p id="agent-hostname-error" className="text-destructive text-sm">
              {t("enrollment.hostnameInvalid")}
            </p>
          )}
        </div>
      </div>

      <AgentFile id="agent-env" title={t("enrollment.env")} copyLabel={t("enrollment.copyEnv")} text={env} />
      <AgentFile id="agent-compose" title={t("enrollment.compose")} copyLabel={t("enrollment.copyCompose")} text={AGENT_COMPOSE_FILE} />
      <p className="text-muted-foreground text-sm">{t("enrollment.steps")}</p>
    </div>
  );
}

function AgentFile({ id, title, copyLabel, text }: { id: string; title: string; copyLabel: string; text: string }) {
  return (
    <div className="grid gap-1.5">
      <div className="flex items-end justify-between gap-2">
        <Label htmlFor={id}>{title}</Label>
        <CopyButton text={text} label={copyLabel} />
      </div>
      <pre id={id} tabIndex={0} className="bg-muted/40 max-h-72 overflow-auto rounded-lg border p-3 font-mono text-xs">
        {text}
      </pre>
    </div>
  );
}
