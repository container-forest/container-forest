/** A date and time in the panel's language, e.g. "October 2, 2026 at 2:05 PM". */
export function formatDateTime(value: string, language: string | undefined): string {
  return new Intl.DateTimeFormat(language, { dateStyle: "long", timeStyle: "short" }).format(new Date(value));
}

/** A date in the panel's language, for columns. */
export function formatDate(value: string, language: string | undefined): string {
  return new Intl.DateTimeFormat(language, { dateStyle: "medium" }).format(new Date(value));
}

/** A date and time to the second, for logs where the order of close events matters. */
export function formatTimestamp(value: string, language: string | undefined): string {
  return new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "medium" }).format(new Date(value));
}
