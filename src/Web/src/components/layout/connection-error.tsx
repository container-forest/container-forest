import { useRouter } from "@tanstack/react-router";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@/api/client";
import { RefreshCwIcon } from "@/components/icons";
import { Brand } from "@/components/layout/brand";
import { Button } from "@/components/ui/button";

/**
 * When the panel itself cannot load: no network (the installed app opened offline) or the aggregator down. It
 * tries again by itself once the device is back online.
 */
export function ConnectionError({ error }: { error: unknown }) {
  const { t } = useTranslation();
  const router = useRouter();
  const offline = !(error instanceof ApiError);

  // Not data loading: the browser says the network is back, and the router loads the page again.
  useEffect(() => {
    const retry = () => void router.invalidate();
    window.addEventListener("online", retry);
    return () => window.removeEventListener("online", retry);
  }, [router]);

  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-6 p-6 text-center">
      <Brand className="text-2xl" />
      <div className="grid max-w-sm gap-2">
        <h1 className="font-heading text-xl font-semibold">{t(offline ? "app.offline.title" : "app.loadFailed.title")}</h1>
        <p className="text-muted-foreground">{t(offline ? "app.offline.description" : "app.loadFailed.description")}</p>
      </div>
      <Button variant="outline" onClick={() => void router.invalidate()}>
        <RefreshCwIcon />
        {t("app.retry")}
      </Button>
    </main>
  );
}
