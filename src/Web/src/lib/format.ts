/** A date and time in the panel's language, e.g. "2 жовтня 2026 р. о 14:05". */
export function formatDateTime(value: string, language: string | undefined): string {
  return new Intl.DateTimeFormat(language, { dateStyle: "long", timeStyle: "short" }).format(new Date(value));
}

/** A date in the panel's language, for columns. */
export function formatDate(value: string, language: string | undefined): string {
  return new Intl.DateTimeFormat(language, { dateStyle: "medium" }).format(new Date(value));
}
