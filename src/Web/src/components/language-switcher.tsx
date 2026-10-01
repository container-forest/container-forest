import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import { type Language, languages } from "@/i18n";

/** Switches the panel between the supported languages; the choice is remembered in this browser. */
export function LanguageSwitcher() {
  const { t, i18n } = useTranslation();
  const current = (i18n.resolvedLanguage ?? "uk") as Language;
  const next = languages[(languages.indexOf(current) + 1) % languages.length]!;

  return (
    <Button variant="ghost" size="sm" lang={next} aria-label={t("language.switchTo")} onClick={() => void i18n.changeLanguage(next)}>
      {t("language.other")}
    </Button>
  );
}
