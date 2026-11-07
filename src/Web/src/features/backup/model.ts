import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// The backup screen without React (ADR-0014): the form, what it sends, how an archive's size reads. The aggregator
// checks every value again; here only what is cheap to say at once.

export type BackupSettings = components["schemas"]["BackupSettingsResponse"];
export type BackupStatus = components["schemas"]["BackupStatusResponse"];
export type BackupRun = components["schemas"]["BackupRunResponse"];

/** BackupSettingsSection.MinIntervalHours and MaxIntervalHours in the aggregator. */
export const MIN_INTERVAL_HOURS = 1;
export const MAX_INTERVAL_HOURS = 168;

export const backupFormSchema = z.object({
  enabled: z.boolean(),
  intervalHours: z
    .string()
    .trim()
    .regex(/^\d+$/, "backup.form.intervalInvalid" satisfies MessageKey)
    .refine((value) => {
      const hours = Number(value);
      return hours >= MIN_INTERVAL_HOURS && hours <= MAX_INTERVAL_HOURS;
    }, "backup.form.intervalInvalid" satisfies MessageKey),
  recipient: z.string().trim(),
  endpoint: z.string().trim(),
  bucket: z.string().trim(),
  region: z.string().trim(),
  prefix: z.string().trim(),
  accessKeyId: z.string().trim(),
  secretAccessKey: z.string(),
  removeSecret: z.boolean(),
});

export type BackupForm = z.infer<typeof backupFormSchema>;

/** The form for the current settings. A secret never comes back, so its field starts empty: empty means "keep it". */
export function backupFormOf(settings: BackupSettings): BackupForm {
  return {
    enabled: settings.enabled.value === "true",
    intervalHours: settings.intervalHours.value ?? "24",
    recipient: settings.recipient.value ?? "",
    endpoint: settings.endpoint.value ?? "",
    bucket: settings.bucket.value ?? "",
    region: settings.region.value ?? "",
    prefix: settings.prefix.value ?? "",
    accessKeyId: settings.accessKeyId.value ?? "",
    secretAccessKey: "",
    removeSecret: false,
  };
}

/** The request body. Null for the key keeps the stored one, an empty string removes it. */
export function toUpdateRequest(form: BackupForm, confirmation: string) {
  return {
    enabled: form.enabled,
    intervalHours: Number(form.intervalHours),
    recipient: form.recipient,
    endpoint: form.endpoint,
    bucket: form.bucket,
    region: form.region,
    prefix: form.prefix,
    accessKeyId: form.accessKeyId,
    secretAccessKey: form.removeSecret ? "" : form.secretAccessKey || null,
    confirmation,
  };
}

/** A size in the largest unit that reads well; the archives are small, so one decimal is enough. */
export function formatSize(size: number | string | null | undefined): string {
  if (size === null || size === undefined) {
    return "—";
  }

  // A 64-bit field: the OpenAPI document allows it as a string too.
  const bytes = Number(size);
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return `${value.toFixed(1)} ${units[unit]}`;
}
