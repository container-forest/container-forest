// Whether it rains where the viewer is, for the rain on the backdrop. Without React: the location from the browser,
// rounded so the exact address never leaves it, and the current weather from Open-Meteo (free, no key; the CSP allows
// only its API). Rain is decoration, so every failure (no permission, no network) simply means no rain.

/** Rain now: none, or how hard on a 0–1 scale (drizzle is light, a downpour is 1). */
export interface Rain {
  raining: boolean;
  intensity: number;
}

export const noRain: Rain = { raining: false, intensity: 0 };

/** How often to look at the sky again. */
export const RAIN_REFRESH_MS = 15 * 60_000;

/** Weather codes (WMO) that mean drizzle, rain, rain showers or a thunderstorm. Snow is not rain. */
const RAIN_CODES = new Set([51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 80, 81, 82, 95, 96, 99]);

/** One decimal place: about 10 km, enough for the weather and far from an address. */
export function roundCoordinate(value: number): number {
  return Math.round(value * 10) / 10;
}

/** Rain from Open-Meteo's `current` block: by the millimetres of liquid in the last hour, or by the weather code. */
export function rainFromCurrent(current: { rain?: number; showers?: number; weather_code?: number } | undefined): Rain {
  if (!current) return noRain;
  const millimetres = (current.rain ?? 0) + (current.showers ?? 0);
  const coded = current.weather_code !== undefined && RAIN_CODES.has(current.weather_code);
  if (millimetres <= 0 && !coded) return noRain;
  // 0.1 mm/h is a drizzle, 4 mm/h and more a heavy rain.
  return { raining: true, intensity: Math.min(1, Math.max(0.25, millimetres / 4)) };
}

export function weatherUrl(latitude: number, longitude: number): string {
  const params = new URLSearchParams({
    latitude: String(roundCoordinate(latitude)),
    longitude: String(roundCoordinate(longitude)),
    current: "rain,showers,weather_code",
  });
  return `https://api.open-meteo.com/v1/forecast?${params}`;
}

/** The browser's idea of where the viewer is, coarse and possibly cached; null without permission or support. */
function position(): Promise<GeolocationPosition | null> {
  if (!("geolocation" in navigator)) return Promise.resolve(null);
  return new Promise((resolve) =>
    navigator.geolocation.getCurrentPosition(resolve, () => resolve(null), {
      enableHighAccuracy: false,
      maximumAge: RAIN_REFRESH_MS * 2,
      timeout: 20_000,
    }),
  );
}

/** In development only: `localStorage` `cf-weather-debug` = `rain` or `downpour` pretends it rains. */
function debugRain(): Rain | null {
  if (!import.meta.env.DEV) return null;
  try {
    const value = localStorage.getItem("cf-weather-debug");
    if (value === "rain") return { raining: true, intensity: 0.4 };
    if (value === "downpour") return { raining: true, intensity: 1 };
  } catch {
    // No storage: the real weather.
  }
  return null;
}

/** Whether it rains at the viewer's location now. The first call may show the browser's location prompt. */
export async function fetchRain(signal: AbortSignal): Promise<Rain> {
  const debug = debugRain();
  if (debug) return debug;
  const where = await position();
  if (!where) return noRain;
  try {
    const response = await fetch(weatherUrl(where.coords.latitude, where.coords.longitude), { signal });
    if (!response.ok) return noRain;
    const body = (await response.json()) as { current?: { rain?: number; showers?: number; weather_code?: number } };
    return rainFromCurrent(body.current);
  } catch {
    return noRain;
  }
}
