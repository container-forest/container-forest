// ANSI escape sequences in container logs: programs that think they write to a terminal colour their output with
// SGR codes (ESC [ … m). The screen either draws those colours or removes every escape sequence.

/** How a run of text looks: colours as CSS values, plus the text attributes the panel can show. */
export interface AnsiStyle {
  color?: string;
  background?: string;
  bold?: boolean;
  dim?: boolean;
  italic?: boolean;
  underline?: boolean;
  strike?: boolean;
  inverse?: boolean;
}

/** A run of text with one style. */
export interface AnsiSegment {
  text: string;
  style: AnsiStyle;
}

// CSI (ESC [ params intermediates final), OSC (ESC ] … BEL or ESC \) and the other two-character escapes. A CSI cut
// off at the end of a line falls through to the two-character case and leaves only its parameters.
const ESCAPE = /\u001b(?:\[([0-?]*)[ -/]*([@-~])|\][^\u0007\u001b]*(?:\u0007|\u001b\\)?|[@-_])/g;

/** The text without escape sequences, as it reads in a terminal without colours. */
export function stripAnsi(text: string): string {
  return text.includes("\u001b") ? text.replace(ESCAPE, "") : text;
}

/**
 * Splits a line into runs of one style. Only SGR sequences (final byte `m`) change the style; other escape
 * sequences (cursor movement, titles) are dropped. Empty runs are left out, and the style carries over to nothing:
 * every log line starts from the default, as Docker hands lines over one by one.
 */
export function parseAnsi(text: string): AnsiSegment[] {
  if (!text.includes("\u001b")) {
    return [{ text, style: {} }];
  }

  const segments: AnsiSegment[] = [];
  let style: AnsiStyle = {};
  let start = 0;
  const push = (end: number) => {
    if (end > start) {
      segments.push({ text: text.slice(start, end), style });
    }
  };

  for (const match of text.matchAll(ESCAPE)) {
    push(match.index);
    start = match.index + match[0].length;
    if (match[2] === "m") {
      style = applySgr(style, match[1] ?? "");
    }
  }

  push(text.length);
  return segments;
}

/** The style after one SGR sequence. Unknown codes are ignored, as terminals do. */
export function applySgr(current: AnsiStyle, parameters: string): AnsiStyle {
  // "ESC [ m" means reset; empty parameters elsewhere count as 0 too.
  const codes = (parameters === "" ? "0" : parameters).split(/[;:]/).map((code) => (code === "" ? 0 : Number(code)));
  let style = { ...current };

  for (let i = 0; i < codes.length; i++) {
    const code = codes[i] ?? 0;
    if (code === 0) {
      style = {};
    } else if (code === 1) {
      style.bold = true;
    } else if (code === 2) {
      style.dim = true;
    } else if (code === 3) {
      style.italic = true;
    } else if (code === 4) {
      style.underline = true;
    } else if (code === 7) {
      style.inverse = true;
    } else if (code === 9) {
      style.strike = true;
    } else if (code === 22) {
      style.bold = undefined;
      style.dim = undefined;
    } else if (code === 23) {
      style.italic = undefined;
    } else if (code === 24) {
      style.underline = undefined;
    } else if (code === 27) {
      style.inverse = undefined;
    } else if (code === 29) {
      style.strike = undefined;
    } else if (code >= 30 && code <= 37) {
      style.color = paletteColor(code - 30);
    } else if (code >= 90 && code <= 97) {
      style.color = paletteColor(code - 90 + 8);
    } else if (code === 39) {
      style.color = undefined;
    } else if (code >= 40 && code <= 47) {
      style.background = paletteColor(code - 40);
    } else if (code >= 100 && code <= 107) {
      style.background = paletteColor(code - 100 + 8);
    } else if (code === 49) {
      style.background = undefined;
    } else if (code === 38 || code === 48) {
      const { color, used } = extendedColor(codes, i + 1);
      i += used;
      if (code === 38) {
        style.color = color;
      } else {
        style.background = color;
      }
    }
  }

  return style;
}

/** `5;n` (256 colours) or `2;r;g;b` (true colour) after 38 or 48: the colour and how many codes it took. */
function extendedColor(codes: readonly number[], at: number): { color: string | undefined; used: number } {
  const kind = codes[at];
  if (kind === 5) {
    const index = codes[at + 1];
    return { color: index === undefined ? undefined : color256(index), used: 2 };
  }

  if (kind === 2) {
    const [r, g, b] = [codes[at + 1], codes[at + 2], codes[at + 3]];
    const valid = [r, g, b].every((channel) => channel !== undefined && channel >= 0 && channel <= 255);
    return { color: valid ? `rgb(${r}, ${g}, ${b})` : undefined, used: 4 };
  }

  return { color: undefined, used: 0 };
}

/** The 16 basic colours come from the theme (`--ansi-0`…`--ansi-15` in styles.css), so they stay readable in both. */
function paletteColor(index: number): string {
  return `var(--ansi-${index})`;
}

/** A colour of the xterm 256-colour palette: 16 theme colours, a 6×6×6 cube and 24 greys. */
export function color256(index: number): string | undefined {
  if (!Number.isInteger(index) || index < 0 || index > 255) {
    return undefined;
  }

  if (index < 16) {
    return paletteColor(index);
  }

  if (index < 232) {
    const level = (n: number) => (n === 0 ? 0 : 55 + n * 40);
    const n = index - 16;
    return `rgb(${level(Math.floor(n / 36))}, ${level(Math.floor(n / 6) % 6)}, ${level(n % 6)})`;
  }

  const grey = 8 + (index - 232) * 10;
  return `rgb(${grey}, ${grey}, ${grey})`;
}
