// Light or dark panel, without React. By default the panel follows the device; the toggle keeps an explicit choice in
// this browser. public/theme-init.js applies the same rule before the first paint, so keep the two in step.

export type Theme = "light" | "dark";

const STORAGE_KEY = "cf-theme";
const DARK_QUERY = "(prefers-color-scheme: dark)";
/** The browser bar colour of each theme: the panel background. */
const THEME_COLORS: Record<Theme, string> = { light: "#f7faf8", dark: "#09130f" };
const listeners = new Set<() => void>();

const isTheme = (value: unknown): value is Theme => value === "light" || value === "dark";

/** The device's theme. */
export function systemTheme(): Theme {
  return matchMedia(DARK_QUERY).matches ? "dark" : "light";
}

/** The theme chosen with the toggle, or null to follow the device. */
function storedTheme(): Theme | null {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return isTheme(stored) ? stored : null;
  } catch {
    return null;
  }
}

let chosen: Theme | null | undefined;

/** The theme the panel shows now. */
export function currentTheme(): Theme {
  if (chosen === undefined) chosen = storedTheme();
  return chosen ?? systemTheme();
}

function apply() {
  const theme = currentTheme();
  document.documentElement.classList.toggle("dark", theme === "dark");
  document.documentElement.style.colorScheme = theme;
  document.querySelector('meta[name="theme-color"]')?.setAttribute("content", THEME_COLORS[theme]);
  listeners.forEach((listener) => listener());
}

/**
 * Switches to the other theme. Picking the device's own theme forgets the choice, so the panel follows the device
 * again (and changes with it, for example at night).
 */
export function toggleTheme() {
  const next: Theme = currentTheme() === "dark" ? "light" : "dark";
  chosen = next === systemTheme() ? null : next;
  try {
    if (chosen) localStorage.setItem(STORAGE_KEY, chosen);
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Not remembered, but still applied until the page reloads.
  }
  apply();
}

/** Applies the theme and keeps following the device while no choice is stored. Call once at start-up. */
export function setUpTheme() {
  matchMedia(DARK_QUERY).addEventListener("change", () => {
    if (!chosen) apply();
  });
  apply();
}

/** For `useSyncExternalStore`. */
export function subscribeTheme(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
