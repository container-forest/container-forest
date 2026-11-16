import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { MetricsRange } from "@/api/queries";
import { EnvironmentMetricsView, RangePicker, StackMetricsView } from "@/components/metrics/metrics-views";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

function MetricsCard({ description, children }: { description: string; children: (range: MetricsRange) => React.ReactNode }) {
  const { t } = useTranslation();
  const [range, setRange] = useState<MetricsRange>("1h");
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("metrics.title")}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-6">
        <RangePicker value={range} onChange={setRange} />
        {children(range)}
      </CardContent>
    </Card>
  );
}

/** A managed stack's resource usage on its page. */
export function StackMetricsCard({ stackId }: { stackId: string }) {
  const { t } = useTranslation();
  return <MetricsCard description={t("metrics.stackDescription")}>{(range) => <StackMetricsView stackId={stackId} range={range} />}</MetricsCard>;
}

/** An environment's host and stacks on its page. */
export function EnvironmentMetricsCard({ environmentId }: { environmentId: string }) {
  const { t } = useTranslation();
  return (
    <MetricsCard description={t("metrics.environmentDescription")}>
      {(range) => <EnvironmentMetricsView environmentId={environmentId} range={range} />}
    </MetricsCard>
  );
}
