// Light or dark panel and its colour theme, without React. By default the panel follows the device in the forest
// colours; the toggle and the theme picker keep explicit choices in this browser. public/theme-init.js applies the
// same rules before the first paint, so keep the two in step.

export type Theme = "light" | "dark";

/**
 * Colour themes: the panel's own forest one, meadow (light on pale green instead of white) and three after Catppuccin
 * (CSS in src/styles.css, `data-palette`).
 */
export const palettes = ["forest", "meadow", "lilac", "lake", "sunset"] as const;
export type Palette = (typeof palettes)[number];

/** Colours of a theme for its preview and the browser bar: background, card and accent, per light and dark. */
export const paletteSwatches: Record<Palette, Record<Theme, { background: string; card: string; accent: string }>> = {
  forest: {
    light: { background: "#f8fbf9", card: "#fdfffd", accent: "#255c45" },
    dark: { background: "#09130f", card: "#0f1a15", accent: "#7cb393" },
  },
  meadow: {
    light: { background: "#e2efdc", card: "#f0f7ec", accent: "#28733a" },
    dark: { background: "#181825", card: "#1e1e2e", accent: "#a6e3a1" },
  },
  lilac: {
    light: { background: "#e6e9ef", card: "#eff1f5", accent: "#6411d0" },
    dark: { background: "#1e2030", card: "#24273a", accent: "#aa71f2" },
  },
  lake: {
    light: { background: "#e6e9ef", card: "#eff1f5", accent: "#0947c3" },
    dark: { background: "#292c3c", card: "#303446", accent: "#7196ea" },
  },
  sunset: {
    light: { background: "#e6e9ef", card: "#eff1f5", accent: "#c64e09" },
    dark: { background: "#181825", card: "#1e1e2e", accent: "#fab387" },
  },
};

const STORAGE_KEY = "cf-theme";
const PALETTE_KEY = "cf-palette";
const DARK_QUERY = "(prefers-color-scheme: dark)";
const listeners = new Set<() => void>();

const isPalette = (value: unknown): value is Palette => (palettes as readonly unknown[]).includes(value);

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

let palette: Palette | undefined;

/** The colour theme the panel shows now. */
export function currentPalette(): Palette {
  if (palette === undefined) {
    try {
      const stored = localStorage.getItem(PALETTE_KEY);
      palette = isPalette(stored) ? stored : "forest";
    } catch {
      palette = "forest";
    }
  }
  return palette;
}

/** Switches the colour theme; the forest one is the default and is not stored. */
export function setPalette(next: Palette) {
  palette = next;
  try {
    if (next === "forest") localStorage.removeItem(PALETTE_KEY);
    else localStorage.setItem(PALETTE_KEY, next);
  } catch {
    // Not remembered, but still applied until the page reloads.
  }
  apply();
}

function apply() {
  const theme = currentTheme();
  const root = document.documentElement;
  root.classList.toggle("dark", theme === "dark");
  root.style.colorScheme = theme;
  const shown = currentPalette();
  if (shown === "forest") delete root.dataset.palette;
  else root.dataset.palette = shown;
  document.querySelector('meta[name="theme-color"]')?.setAttribute("content", paletteSwatches[shown][theme].background);
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
