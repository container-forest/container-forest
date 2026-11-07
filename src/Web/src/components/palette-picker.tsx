import type { CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { usePalette, useTheme } from "@/components/theme-toggle";
import { cn } from "@/lib/utils";
import {
  type Palette,
  type Theme,
  palettes,
  setPalette,
} from "@/features/theme/model";

// Colours come from the themes' own CSS variables: a mushroom's <svg> carries data-palette (and .dark), so
// src/styles.css gives it its theme's colours whatever theme the page shows; the meadow uses the page's theme.
const mix = (color: string, other: string, share: number) =>
  `color-mix(in oklab, ${color} ${100 - share}%, ${other})`;
const fill = (color: string): CSSProperties => ({ fill: color });

/** Spots: off-white. Stem: a light cream warmed by the theme's yellow, and its shaded part. */
const SPOT = "#fffdf6";
const STEM_LIGHT = mix("#f1e3c4", "var(--chart-4)", 10);
const STEM_SHADE = mix(STEM_LIGHT, "#6b5434", 32);

/** Where each mushroom stands on the meadow: size and how far it sits down the slope, so the row looks grown. */
const PLACES: Record<Palette, { scale: number; drop: number }> = {
  forest: { scale: 1, drop: 2 },
  meadow: { scale: 0.86, drop: 10 },
  lilac: { scale: 0.94, drop: 4 },
  lake: { scale: 0.82, drop: 12 },
  sunset: { scale: 0.9, drop: 6 },
};

/** A flat toadstool: a round cap in the theme's accent with off-white spots, and a two-tone stem. Decorative. */
function Mushroom({ palette, theme }: { palette: Palette; theme: Theme }) {
  return (
    <svg
      viewBox="0 0 100 100"
      aria-hidden
      data-palette={palette}
      className={cn("size-full overflow-visible", theme === "dark" && "dark")}
    >
      {/* Stem: the shaded colour, with the light patch over its lower left. */}
      <path
        d="M35 66c0 10-2 18-4 24-1 5 7 8 19 8s20-3 19-8c-2-6-4-14-4-24z"
        style={fill(STEM_SHADE)}
      />
      <path
        d="M33 78c8 2 18 3 26 2 1 5 1 10 0 13-2 3-10 4-17 4-6 0-11-1-11-4 0-5 1-10 2-15z"
        style={fill(STEM_LIGHT)}
      />
      {/* Cap. */}
      <path
        d="M6 58C6 29 26 6 50 6s44 23 44 52c0 9-14 14-44 14S6 67 6 58z"
        style={fill("var(--primary)")}
      />
      {/* Spots of different sizes. */}
      <ellipse cx="47" cy="19" rx="11" ry="7.5" style={fill(SPOT)} />
      <ellipse
        cx="21"
        cy="25"
        rx="5.5"
        ry="3.8"
        transform="rotate(-35 21 25)"
        style={fill(SPOT)}
      />
      <ellipse
        cx="73"
        cy="27"
        rx="9"
        ry="10"
        transform="rotate(-30 73 27)"
        style={fill(SPOT)}
      />
      <ellipse
        cx="19"
        cy="47"
        rx="9"
        ry="11"
        transform="rotate(12 19 47)"
        style={fill(SPOT)}
      />
      <circle cx="51" cy="47" r="12" style={fill(SPOT)} />
      <ellipse
        cx="85"
        cy="50"
        rx="4.2"
        ry="5"
        transform="rotate(-20 85 50)"
        style={fill(SPOT)}
      />
    </svg>
  );
}

/** The meadow behind the mushrooms, in the page's theme: two hills that stretch to any width. */
function Meadow() {
  return (
    <svg
      viewBox="0 0 400 90"
      preserveAspectRatio="none"
      aria-hidden
      className="absolute inset-x-0 bottom-0 h-[72%] w-full"
    >
      <path
        d="M0 40C60 18 120 22 190 34s150 4 210-14V90H0z"
        style={fill(mix("var(--card)", "var(--primary)", 18))}
      />
      <path
        d="M0 58c70-18 140-16 210-6s130 8 190-6V90H0z"
        style={fill(mix("var(--card)", "var(--primary)", 30))}
      />
    </svg>
  );
}

/** A tuft of grass, with a flower in one of the theme's colours if `flower` is given. Keeps its shape at any width. */
function Tuft({ flower }: { flower?: string }) {
  return (
    <svg viewBox="0 0 24 20" aria-hidden className="h-5 w-6">
      <path
        d="M5 20c1-7 3-11 6-13-1 5-1 9 0 13zM11 20c0-5 2-9 5-10-1 4-1 7-1 10zM16 20c0-4 1-6 4-7-1 2-1 4-1 7z"
        style={fill(mix("var(--card)", "var(--primary)", 55))}
      />
      {flower && (
        <>
          <path
            d="M18 14c0-3 0-5 1-7"
            style={{
              stroke: mix("var(--card)", "var(--primary)", 55),
              strokeWidth: 1.2,
              fill: "none",
            }}
          />
          <circle cx="19" cy="6" r="2.6" style={fill(flower)} />
          <circle
            cx="19"
            cy="6"
            r="1"
            style={fill(mix("var(--card)", "var(--chart-4)", 40))}
          />
        </>
      )}
    </svg>
  );
}

/** Where the tufts grow: between the mushrooms (columns end at 20 %, 40 %…) and at the edges, never on a name. */
// On a phone the names nearly touch, so only the tufts at the edges stay there.
const TUFTS: { left: string; flower?: string; wide?: boolean }[] = [
  { left: "3%" },
  { left: "20%", flower: "var(--chart-4)", wide: true },
  { left: "40%", wide: true },
  { left: "60%", flower: "var(--destructive)", wide: true },
  { left: "80%", wide: true },
  { left: "95%", flower: "var(--chart-2)" },
];

/**
 * The colour themes as toadstools growing on one meadow along the bottom of the card, each in the colours of its theme
 * in the current light or dark mode. The chosen one glows. A click switches the theme. A radio group, as any set of
 * radio buttons. Five in a row at any width: they shrink rather than wrap.
 */
export function PalettePicker() {
  const { t } = useTranslation();
  const current = usePalette();
  const theme = useTheme();

  return (
    <div className="relative -mx-(--card-spacing) -mb-(--card-spacing) px-(--card-spacing) pt-3 pb-5">
      <Meadow />
      <div className="relative mx-auto max-w-lg">
        {TUFTS.map(({ left, flower, wide }) => (
          <span
            key={left}
            className={cn("absolute -bottom-3 -translate-x-1/2", wide && "hidden sm:block")}
            style={{ left }}
          >
            <Tuft flower={flower} />
          </span>
        ))}
        <div
          role="radiogroup"
          aria-labelledby="appearance-palette"
          className="relative grid grid-cols-5 items-end gap-1"
        >
          {palettes.map((palette) => {
            const selected = palette === current;
            const { scale, drop } = PLACES[palette];
            return (
              <button
                key={palette}
                type="button"
                role="radio"
                aria-checked={selected}
                title={t(`appearance.palettes.${palette}`)}
                onClick={() => setPalette(palette)}
                style={{ translate: `0 ${drop}px` }}
                className="group/mushroom focus-visible:ring-ring/50 grid min-w-0 justify-items-center gap-1 rounded-xl pb-1 text-xs outline-none focus-visible:ring-3"
              >
                <span className="relative block w-full max-w-16">
                  {/* The chosen mushroom's own glow, in its theme's accent. */}
                  <span
                    data-palette={palette}
                    className={cn(
                      "absolute inset-[-18%] rounded-full transition-opacity duration-300",
                      theme === "dark" && "dark",
                      selected ? "opacity-100" : "opacity-0",
                    )}
                    style={{
                      background:
                        "radial-gradient(closest-side, color-mix(in oklab, var(--primary) 45%, transparent), transparent)",
                    }}
                  />
                  <span
                    className={cn(
                      "relative block aspect-square origin-bottom transition-[translate] duration-200 motion-safe:group-hover/mushroom:animate-[mushroom-sway_0.6s_ease-in-out]",
                      selected && "-translate-y-1.5",
                    )}
                    style={{ scale: String(scale) }}
                  >
                    <Mushroom palette={palette} theme={theme} />
                  </span>
                </span>
                <span
                  className={cn(
                    "relative max-w-full truncate rounded-full px-1.5",
                    selected
                      ? "bg-card/80 text-foreground font-semibold"
                      : "text-muted-foreground",
                  )}
                >
                  {t(`appearance.palettes.${palette}`)}
                </span>
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
