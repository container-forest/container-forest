import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { BackdropCard, PaletteCard } from "@/components/seasons/season-card";

export const Route = createFileRoute("/_authed/appearance")({
  component: AppearanceScreen,
});

/** How the panel looks in this browser: the colour theme and the seasonal backdrop. Nothing here goes to the server. */
function AppearanceScreen() {
  const { t } = useTranslation();

  return (
    <div className="mx-auto grid w-full max-w-2xl gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">{t("appearance.title")}</h1>
        <p className="text-muted-foreground mt-1">{t("appearance.description")}</p>
      </div>

      <BackdropCard />
      {/* Last: the theme mushrooms' meadow closes the page. */}
      <PaletteCard />
    </div>
  );
}
