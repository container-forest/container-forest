// The quiet seasonal backdrop of the panel, without React: which season it is, what the viewer chose, and how the
// particles (leaves, butterflies, snowflakes) move. Drawing lives in the component.

export const seasons = ["spring", "summer", "autumn", "winter"] as const;
export type Season = (typeof seasons)[number];

/** What the viewer chose: follow the calendar, keep one season, or no animation at all. */
export const seasonPreferences = ["auto", ...seasons, "off"] as const;
export type SeasonPreference = (typeof seasonPreferences)[number];

/** Meteorological seasons of the northern hemisphere: winter is December to February. */
export function seasonOf(date: Date): Season {
  const month = date.getMonth();
  if (month >= 2 && month <= 4) return "spring";
  if (month >= 5 && month <= 7) return "summer";
  if (month >= 8 && month <= 10) return "autumn";
  return "winter";
}

/** The season to show, or none. */
export function shownSeason(preference: SeasonPreference, now: Date): Season | null {
  if (preference === "off") return null;
  return preference === "auto" ? seasonOf(now) : preference;
}

// The choice is remembered in this browser only. Storage may be unavailable (private mode, blocked site data), so
// every access is guarded and the default is the calendar.
const STORAGE_KEY = "cf-season";
const listeners = new Set<() => void>();

function isPreference(value: unknown): value is SeasonPreference {
  return (seasonPreferences as readonly unknown[]).includes(value);
}

export function readSeasonPreference(): SeasonPreference {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return isPreference(stored) ? stored : "auto";
  } catch {
    return "auto";
  }
}

export function writeSeasonPreference(preference: SeasonPreference) {
  try {
    localStorage.setItem(STORAGE_KEY, preference);
  } catch {
    // Not remembered, but still applied until the page reloads.
  }
  memory = preference;
  listeners.forEach((listener) => listener());
}

let memory: SeasonPreference | undefined;

/** For `useSyncExternalStore`: the current choice, kept in memory once written so a failed write still applies. */
export function seasonPreferenceSnapshot(): SeasonPreference {
  return memory ?? readSeasonPreference();
}

export function subscribeSeasonPreference(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** How a season's particles look and move. Speeds are in CSS pixels per second. */
export interface SeasonStyle {
  shape: "leaf" | "butterfly" | "snowflake";
  colors: readonly string[];
  /** Particles per 100 000 square pixels of the window, before the limits below. */
  density: number;
  minCount: number;
  maxCount: number;
  size: readonly [number, number];
  fall: readonly [number, number];
  opacity: readonly [number, number];
}

export const seasonStyles: Record<Season, SeasonStyle> = {
  spring: {
    shape: "leaf",
    colors: ["#8fc49a", "#a8d5a2", "#6fae84", "#c3e2b0"],
    density: 1.4,
    minCount: 6,
    maxCount: 18,
    size: [8, 14],
    fall: [14, 28],
    opacity: [0.35, 0.6],
  },
  summer: {
    shape: "butterfly",
    colors: ["#e8c27a", "#d9a7c7", "#9cc5e0", "#f1e3a1"],
    density: 0.8,
    minCount: 4,
    maxCount: 9,
    size: [11, 16],
    fall: [-6, 6],
    opacity: [0.45, 0.7],
  },
  autumn: {
    shape: "leaf",
    colors: ["#b5462f", "#c9692f", "#d98b3a", "#8f2f24", "#c7a04a"],
    density: 1.4,
    minCount: 6,
    maxCount: 18,
    size: [9, 15],
    fall: [16, 32],
    opacity: [0.35, 0.6],
  },
  winter: {
    shape: "snowflake",
    colors: ["#f2f7f4", "#dfeee6", "#ffffff"],
    density: 2,
    minCount: 10,
    maxCount: 28,
    size: [2, 6],
    fall: [12, 26],
    opacity: [0.3, 0.6],
  },
};

export interface Particle {
  x: number;
  y: number;
  size: number;
  color: string;
  opacity: number;
  /** Downward speed; butterflies use it as a slow drift instead. */
  fall: number;
  /** Sideways sway: amplitude in pixels and angular speed. */
  sway: number;
  swaySpeed: number;
  rotation: number;
  spin: number;
  /** Phase of the sway and, for butterflies, of the wing beat. */
  phase: number;
  /** Butterflies wander: their current heading in radians. */
  heading: number;
}

export type Random = () => number;

const between = (random: Random, [min, max]: readonly [number, number]) => min + random() * (max - min);

export function particleCount(style: SeasonStyle, width: number, height: number): number {
  const count = Math.round(((width * height) / 100_000) * style.density);
  return Math.min(style.maxCount, Math.max(style.minCount, count));
}

/**
 * A new particle. At the start they are spread over the whole window; later ones enter above the top edge
 * (butterflies anywhere, as they fly in from the side).
 */
export function spawnParticle(style: SeasonStyle, width: number, height: number, random: Random, initial: boolean): Particle {
  const size = between(random, style.size);
  const fromLeft = random() < 0.5;
  // A butterfly that is not there from the start flies in from a side edge, towards the other one.
  const sideEntry = style.shape === "butterfly" && !initial;
  return {
    x: sideEntry ? (fromLeft ? -size * 2 : width + size * 2) : random() * width,
    y: initial || sideEntry ? random() * height : -size * 2,
    size,
    color: style.colors[Math.floor(random() * style.colors.length)] ?? style.colors[0]!,
    opacity: between(random, style.opacity),
    fall: between(random, style.fall),
    sway: style.shape === "snowflake" ? between(random, [4, 14]) : between(random, [12, 36]),
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
  return particle.y < height + particle.size * 3;
}

/** Where to draw a particle now, with its sway applied. */
export function swayOffset(particle: Particle): number {
  return Math.sin(particle.phase) * particle.sway;
}
