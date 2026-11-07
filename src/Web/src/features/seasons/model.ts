// The quiet seasonal backdrop of the panel, without React: which season it is, what the viewer chose, and how the
// particles (leaves, butterflies, poplar fluff, snowflakes, rain) move. Drawing lives in the component.

export const seasons = ["spring", "summer", "autumn", "winter"] as const;
export type Season = (typeof seasons)[number];

/** What the backdrop can show: the four seasons, and a downpour that only the viewer turns on (the calendar never does). */
export const scenes = [...seasons, "downpour"] as const;
export type Scene = (typeof scenes)[number];

/** Which scene to show: the season the calendar says, or one the viewer keeps all year. */
export const seasonChoices = ["auto", ...scenes] as const;
export type SeasonChoice = (typeof seasonChoices)[number];

/** Every kind of particle; a season shows one or more of them. */
export const effects = ["springLeaves", "butterflies", "fluff", "autumnLeaves", "snowflakes", "rain", "downpour"] as const;
export type Effect = (typeof effects)[number];

/** What each scene shows. Summer has two layers: butterflies and the poplar fluff around them. */
export const seasonEffects: Record<Scene, readonly Effect[]> = {
  spring: ["springLeaves"],
  summer: ["butterflies", "fluff"],
  autumn: ["autumnLeaves"],
  winter: ["snowflakes"],
  downpour: ["downpour"],
};

/**
 * Everything the viewer set. The default is the animation on, by the calendar, with rain when it rains outside.
 * Rain does not depend on the seasonal animation: either can be on alone.
 */
export interface BackdropSettings {
  enabled: boolean;
  season: SeasonChoice;
  rain: boolean;
}

export const defaultBackdropSettings: BackdropSettings = { enabled: true, season: "auto", rain: true };

/** Meteorological seasons of the northern hemisphere: winter is December to February. */
export function seasonOf(date: Date): Season {
  const month = date.getMonth();
  if (month >= 2 && month <= 4) return "spring";
  if (month >= 5 && month <= 7) return "summer";
  if (month >= 8 && month <= 10) return "autumn";
  return "winter";
}

/** The scene to show, or none when the animation is off. */
export function shownSeason(settings: BackdropSettings, now: Date): Scene | null {
  if (!settings.enabled) return null;
  return settings.season === "auto" ? seasonOf(now) : settings.season;
}

/**
 * The effects to draw now: those of the shown scene, and rain on top when it rains and the viewer wants it; not over a
 * downpour, which is already wetter than any weather.
 */
export function shownEffects(settings: BackdropSettings, now: Date, raining = false): readonly Effect[] {
  const season = shownSeason(settings, now);
  const seasonal = season ? seasonEffects[season] : [];
  return settings.rain && raining && season !== "downpour" ? [...seasonal, "rain"] : seasonal;
}

// The settings are remembered in this browser only. Storage may be unavailable (private mode, blocked site data), so
// every access is guarded and anything unreadable falls back to the defaults.
const STORAGE_KEY = "cf-backdrop";
// Before the settings had their own record, only the season was kept, with "off" for no animation.
const LEGACY_KEY = "cf-season";
const listeners = new Set<() => void>();

const isSeasonChoice = (value: unknown): value is SeasonChoice => (seasonChoices as readonly unknown[]).includes(value);

/** Settings from their stored form; unknown or broken values fall back to the defaults one by one, and other fields are ignored. */
export function parseBackdropSettings(stored: string | null, legacy: string | null): BackdropSettings {
  if (stored !== null) {
    try {
      const value: unknown = JSON.parse(stored);
      if (typeof value === "object" && value !== null) {
        const { enabled, season, rain } = value as Record<string, unknown>;
        return {
          enabled: typeof enabled === "boolean" ? enabled : defaultBackdropSettings.enabled,
          season: isSeasonChoice(season) ? season : defaultBackdropSettings.season,
          rain: typeof rain === "boolean" ? rain : defaultBackdropSettings.rain,
        };
      }
    } catch {
      // Not JSON: the defaults.
    }
    return defaultBackdropSettings;
  }
  if (legacy === "off") return { ...defaultBackdropSettings, enabled: false };
  if (isSeasonChoice(legacy)) return { ...defaultBackdropSettings, season: legacy };
  return defaultBackdropSettings;
}

function readBackdropSettings(): BackdropSettings {
  try {
    return parseBackdropSettings(localStorage.getItem(STORAGE_KEY), localStorage.getItem(LEGACY_KEY));
  } catch {
    return defaultBackdropSettings;
  }
}

let memory: BackdropSettings | undefined;

/** For `useSyncExternalStore`: the same object until the settings change, kept in memory so a failed write still applies. */
export function backdropSettingsSnapshot(): BackdropSettings {
  memory ??= readBackdropSettings();
  return memory;
}

export function subscribeBackdropSettings(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Changes part of the settings, remembers them and redraws the backdrop. */
export function updateBackdropSettings(change: Partial<BackdropSettings>) {
  memory = { ...backdropSettingsSnapshot(), ...change };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(memory));
    localStorage.removeItem(LEGACY_KEY);
  } catch {
    // Not remembered, but still applied until the page reloads.
  }
  listeners.forEach((listener) => listener());
}

/** How an effect's particles look and move. Speeds are in CSS pixels per second. */
export interface SeasonStyle {
  shape: "leaf" | "butterfly" | "fluff" | "snowflake" | "rain";
  colors: readonly string[];
  /** Colours on the light theme, for particles that would vanish on a light background. */
  lightColors?: readonly string[];
  /** Particles per 100 000 square pixels of the window, before the limits below. */
  density: number;
  minCount: number;
  maxCount: number;
  size: readonly [number, number];
  /** Downward speed; a negative one rises. */
  fall: readonly [number, number];
  opacity: readonly [number, number];
}

export const effectStyles: Record<Effect, SeasonStyle> = {
  springLeaves: {
    shape: "leaf",
    colors: ["#8fc49a", "#a8d5a2", "#6fae84", "#c3e2b0"],
    density: 3.5,
    minCount: 15,
    maxCount: 45,
    size: [8, 14],
    fall: [14, 28],
    opacity: [0.35, 0.6],
  },
  butterflies: {
    shape: "butterfly",
    colors: ["#e8c27a", "#d9a7c7", "#9cc5e0", "#f1e3a1"],
    density: 1.6,
    minCount: 8,
    maxCount: 18,
    size: [11, 16],
    fall: [-6, 6],
    opacity: [0.45, 0.7],
  },
  fluff: {
    // Poplar fluff: tiny bright specks that hang in the air, drift with a light breeze and sometimes rise.
    shape: "fluff",
    colors: ["#fffbe6", "#fff3b8", "#ffffff", "#ffe9a3"],
    lightColors: ["#c99a2e", "#d8a93c", "#b8892a", "#e0b54a"],
    density: 5.5,
    minCount: 30,
    maxCount: 85,
    size: [1.4, 3.2],
    fall: [-5, 9],
    opacity: [0.55, 0.95],
  },
  autumnLeaves: {
    shape: "leaf",
    colors: ["#b5462f", "#c9692f", "#d98b3a", "#8f2f24", "#c7a04a"],
    density: 3.5,
    minCount: 15,
    maxCount: 45,
    size: [9, 15],
    fall: [16, 32],
    opacity: [0.35, 0.6],
  },
  snowflakes: {
    shape: "snowflake",
    colors: ["#f2f7f4", "#dfeee6", "#ffffff"],
    lightColors: ["#9fb8c4", "#b3c8d1", "#8fa9b6"],
    density: 7.5,
    minCount: 38,
    maxCount: 105,
    size: [2, 6],
    fall: [12, 26],
    opacity: [0.3, 0.6],
  },
  rain: {
    // Thin streaks falling fast and a little slanted, as in a light wind. `size` is the length of a streak.
    shape: "rain",
    colors: ["#b9d3e4", "#a6c3d8", "#cfe0ec"],
    lightColors: ["#6d8ea3", "#5f8197", "#7d9bae"],
    density: 6,
    minCount: 36,
    maxCount: 130,
    size: [14, 30],
    fall: [560, 820],
    opacity: [0.35, 0.65],
  },
  downpour: {
    // Heavy rain the viewer chooses: thick, long, fast streaks, several times the weather's heaviest rain.
    shape: "rain",
    colors: ["#c6dcea", "#b0cade", "#d8e7f1", "#9fbcd2"],
    lightColors: ["#5f8197", "#527489", "#6d8ea3"],
    density: 16,
    minCount: 110,
    maxCount: 380,
    size: [26, 52],
    fall: [1000, 1400],
    opacity: [0.4, 0.75],
  },
};

/** How slanted the rain falls: sideways pixels per pixel of fall. */
export const RAIN_SLANT = 0.12;

export interface Particle {
  x: number;
  y: number;
  size: number;
  color: string;
  opacity: number;
  /** Downward speed (negative rises); butterflies use it as a slow drift instead. */
  fall: number;
  /** Sideways sway: amplitude in pixels and angular speed. */
  sway: number;
  swaySpeed: number;
  rotation: number;
  spin: number;
  /** Phase of the sway and, for butterflies, of the wing beat. */
  phase: number;
  /** Butterflies wander and fluff drifts with the breeze: the current heading in radians. */
  heading: number;
}

export type Random = () => number;

const between = (random: Random, [min, max]: readonly [number, number]) => min + random() * (max - min);

/** How many particles fill a window; `scale` thins or thickens the layer (rain by its intensity). */
export function particleCount(style: SeasonStyle, width: number, height: number, scale = 1): number {
  const count = Math.round(((width * height) / 100_000) * style.density);
  return Math.round(Math.min(style.maxCount, Math.max(style.minCount, count)) * scale);
}

/**
 * A new particle. At the start they are spread over the whole window; later ones enter above the top edge
 * (butterflies anywhere, as they fly in from the side).
 */
export function spawnParticle(
  style: SeasonStyle,
  width: number,
  height: number,
  random: Random,
  initial: boolean,
  light = false,
): Particle {
  const size = between(random, style.size);
  const colors = (light && style.lightColors) || style.colors;
  const fall = between(random, style.fall);
  const fromLeft = random() < 0.5;
  // A butterfly that is not there from the start flies in from a side edge, towards the other one.
  const sideEntry = style.shape === "butterfly" && !initial;
  return {
    x: sideEntry ? (fromLeft ? -size * 2 : width + size * 2) : random() * width,
    // Rising fluff comes in from below the bottom edge.
    y: initial || sideEntry ? random() * height : fall < 0 ? height + size * 2 : -size * 2,
    size,
    color: colors[Math.floor(random() * colors.length)] ?? colors[0]!,
    opacity: between(random, style.opacity),
    fall,
    sway:
      style.shape === "rain"
        ? 0
        : style.shape === "snowflake"
          ? between(random, [4, 14])
          : style.shape === "fluff"
            ? between(random, [10, 28])
            : between(random, [12, 36]),
    swaySpeed: between(random, [0.4, 1.1]),
    rotation: random() * Math.PI * 2,
    spin: style.shape === "leaf" ? between(random, [-0.9, 0.9]) : 0,
    phase: random() * Math.PI * 2,
    heading: sideEntry ? (fromLeft ? 0 : Math.PI) + (random() - 0.5) * 0.8 : random() * Math.PI * 2,
  };
}

/**
 * Moves a particle by `dt` seconds. Returns false when it has left the window and should be replaced. The sway is
 * not accumulated into `x`, so it never drifts the particle off course.
 */
export function stepParticle(particle: Particle, style: SeasonStyle, dt: number, width: number, height: number, random: Random): boolean {
  particle.phase += particle.swaySpeed * dt;
  if (style.shape === "butterfly") {
    // A slow wander: the heading turns a little every frame, and the speed stays gentle.
    particle.heading += (random() - 0.5) * 1.6 * dt;
    const speed = 18 + Math.abs(particle.fall);
    particle.x += Math.cos(particle.heading) * speed * dt;
    particle.y += Math.sin(particle.heading) * speed * 0.6 * dt;
    const margin = particle.size * 3;
    return particle.x > -margin && particle.x < width + margin && particle.y > -margin && particle.y < height + margin;
  }

  particle.y += particle.fall * dt;
  particle.rotation += particle.spin * dt;
  if (style.shape === "rain") {
    // Slanted to the right; a drop that leaves at the right edge comes back at the left, so the left stays wet.
    particle.x += particle.fall * RAIN_SLANT * dt;
    if (particle.x > width + particle.size) particle.x -= width + particle.size * 2;
    return particle.y < height + particle.size * 2;
  }
  if (style.shape === "fluff") {
    // A breeze that slowly changes direction; fluff that drifts out at one side comes back at the other.
    particle.heading += (random() - 0.5) * 0.8 * dt;
    particle.x += Math.cos(particle.heading) * 7 * dt;
    const margin = particle.size * 3 + particle.sway;
    if (particle.x < -margin) particle.x = width + margin;
    else if (particle.x > width + margin) particle.x = -margin;
    return particle.y < height + particle.size * 3 && particle.y > -particle.size * 3;
  }
  return particle.y < height + particle.size * 3;
}

/** Where to draw a particle now, with its sway applied. */
export function swayOffset(particle: Particle): number {
  return Math.sin(particle.phase) * particle.sway;
}
