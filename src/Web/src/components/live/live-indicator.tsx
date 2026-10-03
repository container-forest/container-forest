import { useTranslation } from "react-i18next";
import { useLiveStatus } from "./hub-provider";

const dotClass = {
  live: "bg-emerald-500",
  connecting: "bg-muted-foreground",
  reconnecting: "bg-amber-500 animate-pulse",
  offline: "bg-muted-foreground",
} as const;

/** Whether the screen updates by itself; while reconnecting (e.g. the aggregator is being updated) it says so. */
export function LiveIndicator() {
  const { t } = useTranslation();
  const status = useLiveStatus();

  return (
    <span
      role="status"
      className="text-muted-foreground flex items-center gap-1.5 text-xs"
      title={t(`live.hint.${status}`)}
    >
      <span aria-hidden className={`size-2 rounded-full ${dotClass[status]}`} />
      <span className={status === "live" ? "sr-only sm:not-sr-only" : undefined}>{t(`live.status.${status}`)}</span>
    </span>
  );
}
