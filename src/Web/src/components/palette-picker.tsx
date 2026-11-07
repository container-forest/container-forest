import { useTranslation } from "react-i18next";
import { usePalette, useTheme } from "@/components/theme-toggle";
import { CheckIcon } from "@/components/icons";
import { cn } from "@/lib/utils";
import { palettes, paletteSwatches, setPalette } from "@/features/theme/model";

/**
 * The colour themes as small previews in the current light or dark mode: background, a card on it and the accent.
 * A radio group: arrows move between themes, as with any radio buttons.
 */
export function PalettePicker() {
  const { t } = useTranslation();
  const current = usePalette();
  const theme = useTheme();

  return (
    <div role="radiogroup" aria-labelledby="appearance-palette" className="grid grid-cols-2 gap-2 sm:grid-cols-5">
      {palettes.map((palette) => {
        const swatch = paletteSwatches[palette][theme];
        const selected = palette === current;
        return (
          <button
            key={palette}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => setPalette(palette)}
            className={cn(
              "focus-visible:ring-ring/50 grid gap-2 rounded-lg border p-2 text-left text-sm transition-colors outline-none focus-visible:ring-3",
              selected ? "border-primary ring-primary ring-1" : "hover:bg-accent",
            )}
          >
            <span aria-hidden className="flex h-10 items-end gap-1 rounded-md p-1.5" style={{ background: swatch.background }}>
              <span className="h-full flex-1 rounded-sm" style={{ background: swatch.card }} />
              <span className="size-4 rounded-full" style={{ background: swatch.accent }} />
            </span>
            <span className="flex items-center justify-between gap-1">
              <span className="truncate font-medium">{t(`appearance.palettes.${palette}`)}</span>
              {selected && <CheckIcon className="text-primary size-4 shrink-0" />}
            </span>
          </button>
        );
      })}
    </div>
  );
}
