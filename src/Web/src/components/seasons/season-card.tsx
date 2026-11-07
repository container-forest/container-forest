import { useTranslation } from "react-i18next";
import { useBackdropSettings } from "@/components/seasons/seasonal-backdrop";
import { PalettePicker } from "@/components/palette-picker";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { type SeasonChoice, seasonChoices, seasonOf, updateBackdropSettings } from "@/features/seasons/model";

function isSeasonChoice(value: string): value is SeasonChoice {
  return (seasonChoices as readonly string[]).includes(value);
}

/**
 * The seasonal backdrop: on or off, which season (by the calendar or one kept all year), and rain when it rains where the viewer is.
 * All on, by the calendar, unless the viewer changes it. Remembered in this browser.
 */
export function BackdropCard() {
  const { t } = useTranslation();
  const settings = useBackdropSettings();
  const current = t(`appearance.current.${seasonOf(new Date())}`);

  // Radix calls this with "" and with the current value too, not only on a choice.
  const onSeasonChange = (value: string) => {
    if (isSeasonChoice(value) && value !== settings.season) updateBackdropSettings({ season: value });
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("appearance.backdrop")}</CardTitle>
        <CardDescription>{t("appearance.backdropDescription")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-5">
        <div className="flex items-center justify-between gap-4">
          <Label htmlFor="appearance-enabled">{t("appearance.enabled")}</Label>
          <Switch
            id="appearance-enabled"
            checked={settings.enabled}
            onCheckedChange={(enabled) => updateBackdropSettings({ enabled })}
          />
        </div>

        <div className="grid gap-2">
          <Label htmlFor="appearance-season">{t("appearance.season")}</Label>
          <Select value={settings.season} onValueChange={onSeasonChange} disabled={!settings.enabled}>
            <SelectTrigger id="appearance-season" className="w-full sm:w-80">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {seasonChoices.map((option) => (
                <SelectItem key={option} value={option}>
                  {option === "auto" ? t("appearance.auto", { season: current }) : t(`appearance.seasons.${option}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="grid gap-1.5">
          <div className="flex items-center justify-between gap-4">
            <Label htmlFor="appearance-rain">{t("appearance.rain")}</Label>
            <Switch id="appearance-rain" checked={settings.rain} onCheckedChange={(rain) => updateBackdropSettings({ rain })} />
          </div>
          <p className="text-muted-foreground text-sm">
            {t("appearance.rainHint")}{" "}
            <a href="https://open-meteo.com/" target="_blank" rel="noreferrer" className="underline underline-offset-2">
              {t("appearance.rainSource")}
            </a>
          </p>
        </div>

        <p className="text-muted-foreground text-sm">{t("appearance.reducedMotion")}</p>
      </CardContent>
    </Card>
  );
}

/** The colour theme: mushrooms on a meadow along the bottom edge of the card, one per palette. */
export function PaletteCard() {
  const { t } = useTranslation();

  return (
    <Card>
      <CardHeader>
        <CardTitle id="appearance-palette">{t("appearance.palette")}</CardTitle>
        <CardDescription>{t("appearance.paletteCredit")}</CardDescription>
      </CardHeader>
      <CardContent>
        <PalettePicker />
      </CardContent>
    </Card>
  );
}
