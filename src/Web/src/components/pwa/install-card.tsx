import { useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { CheckCircleIcon, InstallDesktopIcon, ShareIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { install, installState, subscribeInstallState } from "@/features/pwa/model";

/** Installing the panel as an app: a button where the browser allows it, the steps on iPhone, or that it is done. */
export function InstallCard() {
  const { t } = useTranslation();
  const state = useSyncExternalStore(subscribeInstallState, installState);
  const [dismissed, setDismissed] = useState(false);

  const onInstall = async () => setDismissed(!(await install()));

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("pwa.title")}</CardTitle>
        <CardDescription>{t("pwa.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <div className="flex items-center gap-3">
          <img src="/icons/icon.svg" alt="" className="size-12 shrink-0" />
          <div className="min-w-0">
            <p className="font-medium">container-forest</p>
            <p className="text-muted-foreground text-sm">{t(`pwa.state.${state}`)}</p>
          </div>
        </div>
        {state === "available" && (
          <Button className="justify-self-start" onClick={() => void onInstall()}>
            <InstallDesktopIcon />
            {t("pwa.install")}
          </Button>
        )}
        {state === "installed" && (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <CheckCircleIcon className="text-primary size-5" />
            {t("pwa.installedHint")}
          </p>
        )}
        {state === "ios" && (
          <p className="text-muted-foreground text-sm">
            {t("pwa.iosBefore")} <ShareIcon aria-label={t("pwa.share")} className="inline size-4 align-text-bottom" /> {t("pwa.iosAfter")}
          </p>
        )}
        {(state === "unsupported" || dismissed) && state !== "installed" && (
          <p className="text-muted-foreground text-sm">{t("pwa.browserMenu")}</p>
        )}
      </CardContent>
    </Card>
  );
}
