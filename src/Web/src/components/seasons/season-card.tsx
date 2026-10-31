import { useTranslation } from "react-i18next";
import { useSeasonPreference } from "@/components/seasons/seasonal-backdrop";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { type SeasonPreference, seasonOf, seasonPreferences, writeSeasonPreference } from "@/features/seasons/model";

function isPreference(value: string): value is SeasonPreference {
  return (seasonPreferences as readonly string[]).includes(value);
}

/** The seasonal backdrop: by the calendar, one chosen season, or off. Remembered in this browser. */
export function SeasonCard() {
  const { t } = useTranslation();
  const preference = useSeasonPreference();
  const current = t(`appearance.current.${seasonOf(new Date())}`);

  // Radix calls this with "" and with the current value too, not only on a choice.
  const onChange = (value: string) => {
    if (isPreference(value) && value !== preference) writeSeasonPreference(value);
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("appearance.title")}</CardTitle>
        <CardDescription>{t("appearance.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-2">
        <Label htmlFor="appearance-season">{t("appearance.season")}</Label>
        <Select value={preference} onValueChange={onChange}>
          <SelectTrigger id="appearance-season" className="w-full sm:w-64">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {seasonPreferences.map((option) => (
              <SelectItem key={option} value={option}>
                {option === "auto"
                  ? t("appearance.auto", { season: current })
                  : option === "off"
                    ? t("appearance.off")
                    : t(`appearance.seasons.${option}`)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <p className="text-muted-foreground text-sm">{t("appearance.reducedMotion")}</p>
      </CardContent>
    </Card>
  );
}
