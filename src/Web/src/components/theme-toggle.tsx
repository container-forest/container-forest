import { useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { DarkModeIcon, LightModeIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { currentTheme, subscribeTheme, toggleTheme } from "@/features/theme/model";

/** The theme the panel shows now: the device's, or the one chosen with the toggle. */
export function useTheme() {
  return useSyncExternalStore(subscribeTheme, currentTheme);
}

/** Switches between the light and the dark panel; shows the theme it switches to. */
export function ThemeToggle() {
  const { t } = useTranslation();
  const dark = useTheme() === "dark";
  const label = dark ? t("theme.toLight") : t("theme.toDark");

  return (
    <Button variant="ghost" size="icon-sm" aria-label={label} title={label} onClick={toggleTheme}>
      {dark ? <LightModeIcon /> : <DarkModeIcon />}
    </Button>
  );
}
