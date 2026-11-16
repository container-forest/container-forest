import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "@/api/client";
import { metricsSettingsQuery } from "@/api/queries";
import type { components } from "@/api/schema.gen";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { describeError } from "@/features/stacks/model";
import type { MessageKey } from "@/i18n";

type Request = components["schemas"]["UpdateMetricsSettingsRequest"];
type NumberField = Exclude<keyof Request, "alertsEnabled">;

const numberFields: readonly { field: NumberField; label: MessageKey }[] = [
  { field: "minuteRetentionDays", label: "metrics.settings.minuteRetentionDays" },
  { field: "hourRetentionDays", label: "metrics.settings.hourRetentionDays" },
  { field: "hostCpuPercent", label: "metrics.settings.hostCpuPercent" },
  { field: "hostMemoryPercent", label: "metrics.settings.hostMemoryPercent" },
  { field: "diskPercent", label: "metrics.settings.diskPercent" },
  { field: "containerMemoryPercent", label: "metrics.settings.containerMemoryPercent" },
  { field: "alertMinutes", label: "metrics.settings.alertMinutes" },
];

/**
 * Retention and alert thresholds of resource usage (ADR-0017), for administrators. A field set with CF_METRICS__* is
 * locked here; the aggregator validates the ranges.
 */
export function MetricsSettingsCard() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const settings = useQuery(metricsSettingsQuery);
  const [draft, setDraft] = useState<Partial<Record<keyof Request, string | boolean>>>({});
  const save = useMutation({
    mutationFn: async (request: Request) => unwrap(await api.PUT("/api/settings/metrics", { body: request })),
    onSuccess: (saved) => {
      queryClient.setQueryData(metricsSettingsQuery.queryKey, saved);
      setDraft({});
    },
  });

  if (settings.isPending) {
    return <Loader2Icon className="text-muted-foreground size-5 animate-spin" aria-hidden />;
  }
  if (settings.isError) {
    return null;
  }

  const data = settings.data;
  const value = (field: NumberField) => String(draft[field] ?? data[field].value ?? "");
  const alerts = (draft.alertsEnabled as boolean | undefined) ?? data.alertsEnabled.value !== "false";
  const error = save.error ? describeError(save.error) : undefined;
  const submit = () =>
    save.mutate({
      ...(Object.fromEntries(numberFields.map(({ field }) => [field, Number(value(field))])) as Record<NumberField, number>),
      alertsEnabled: alerts,
    });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("metrics.settings.title")}</CardTitle>
        <CardDescription>{t("metrics.settings.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="flex items-center gap-2">
          <Switch
            id="metrics-alerts"
            checked={alerts}
            disabled={data.alertsEnabled.locked}
            onCheckedChange={(checked) => setDraft((d) => ({ ...d, alertsEnabled: checked === true }))}
          />
          <Label htmlFor="metrics-alerts">{t("metrics.settings.alertsEnabled")}</Label>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          {numberFields.map(({ field, label }) => (
            <div key={field} className="grid gap-1.5">
              <Label htmlFor={`metrics-${field}`}>{t(label)}</Label>
              <Input
                id={`metrics-${field}`}
                type="number"
                inputMode="numeric"
                min={0}
                value={value(field)}
                disabled={data[field].locked}
                onChange={(e) => setDraft((d) => ({ ...d, [field]: e.target.value }))}
              />
              {data[field].locked ? <p className="text-muted-foreground text-xs">{t("metrics.settings.locked")}</p> : null}
            </div>
          ))}
        </div>
        <p className="text-muted-foreground text-xs">{t("metrics.settings.thresholdHint")}</p>
        {error ? <ErrorText>{error.detail ?? t(error.key)}</ErrorText> : null}
        <div>
          <Button onClick={submit} disabled={save.isPending || Object.keys(draft).length === 0}>
            {save.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : null}
            {t("metrics.settings.save")}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
