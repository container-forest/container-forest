import { Link } from "@tanstack/react-router";
import { cn } from "cn";
import { useTranslation } from "react-i18next";

/** Logs and Metrics of one container, as two pages with the same header. */
export function ContainerTabs({
  environmentId,
  containerId,
  active,
  from,
}: {
  environmentId: string;
  containerId: string;
  active: "logs" | "metrics";
  /** The page the container was opened from: switching tabs keeps the back link. */
  from: string | undefined;
}) {
  const { t } = useTranslation();
  const tab = (selected: boolean) =>
    cn(
      "border-b-2 px-1 pb-2 text-sm font-medium transition-colors",
      selected ? "border-primary text-foreground" : "text-muted-foreground hover:text-foreground border-transparent",
    );
  return (
    <nav aria-label={t("metrics.tabs.label")} className="border-border flex gap-4 border-b">
      <Link
        to="/containers/$environmentId/$containerId/logs"
        params={{ environmentId, containerId }}
        search={{ from }}
        className={tab(active === "logs")}
        aria-current={active === "logs" ? "page" : undefined}
      >
        {t("metrics.tabs.logs")}
      </Link>
      <Link
        to="/containers/$environmentId/$containerId/metrics"
        params={{ environmentId, containerId }}
        search={{ from }}
        className={tab(active === "metrics")}
        aria-current={active === "metrics" ? "page" : undefined}
      >
        {t("metrics.tabs.metrics")}
      </Link>
    </nav>
  );
}
