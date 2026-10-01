import { useEffect, useState } from "react";

/** Whole seconds until `expiresAt` (ISO 8601), updated every second; 0 once it has passed, null without a time. */
export function useSecondsLeft(expiresAt: string | undefined): number | null {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (expiresAt === undefined) {
      return;
    }

    setNow(Date.now());
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [expiresAt]);

  return expiresAt === undefined ? null : Math.max(0, Math.ceil((Date.parse(expiresAt) - now) / 1000));
}

/** 125 → "2:05". */
export function formatMinutesSeconds(totalSeconds: number): string {
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, "0")}`;
}
