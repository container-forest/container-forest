import { z } from "zod";
import { ApiError } from "@/api/client";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// What the notification screens show and allow, without React: forms, scopes, filters, error texts.

export type NotificationChannel = components["schemas"]["NotificationChannelResponse"];
export type NotificationRule = components["schemas"]["NotificationRuleResponse"];
export type Notification = components["schemas"]["NotificationResponse"];
export type NotificationStatus = components["schemas"]["NotificationStatus"];
export type NotificationSeverity = components["schemas"]["NotificationSeverity"];
export type CreateNotificationRuleRequest = components["schemas"]["CreateNotificationRuleRequest"];

/** The single kinds a rule can subscribe to, in the aggregator's order (NotificationEventKinds without None and All). */
export const notificationEventKinds = [
  "DeploymentSucceeded",
  "DeploymentFailed",
  "ContainerDied",
  "ContainerUnhealthy",
  "AgentOffline",
  "AgentOnline",
] as const;

export type NotificationEventKind = (typeof notificationEventKinds)[number];

/** Kinds the aggregator raises today; the rest wait for the agent's container events and are marked in the form. */
const raisedKinds: ReadonlySet<string> = new Set<NotificationEventKind>(["DeploymentSucceeded", "DeploymentFailed"]);

export function isRaisedYet(kind: NotificationEventKind): boolean {
  return raisedKinds.has(kind);
}

/** A kind from the aggregator that this panel knows; a newer aggregator may send one it does not. */
export function isKnownKind(kind: string): kind is NotificationEventKind {
  return (notificationEventKinds as readonly string[]).includes(kind);
}

export const notificationStatuses = ["Pending", "Delivered", "Failed", "Expired"] as const satisfies readonly NotificationStatus[];

/** NotificationChannel.MaxNameLength in the aggregator. */
export const MAX_CHANNEL_NAME = 64;

/**
 * A Discord channel id: a snowflake, 17 to 20 digits since 2015. Kept as a string end to end: as a JavaScript
 * number its last digits would change.
 */
export const discordIdPattern = /^[1-9]\d{16,19}$/;

export const channelSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, "notifications.channels.form.nameInvalid" satisfies MessageKey)
    .max(MAX_CHANNEL_NAME, "notifications.channels.form.nameInvalid" satisfies MessageKey),
  discordChannelId: z
    .string()
    .trim()
    .regex(discordIdPattern, "notifications.channels.form.discordIdInvalid" satisfies MessageKey),
});

export type ChannelForm = z.infer<typeof channelSchema>;

/** Keeps only digits while typing: a pasted id often brings spaces or a newline with it. */
export function digitsOnly(value: string): string {
  return value.replace(/\D/g, "");
}

export const eventsSchema = z
  .array(z.enum(notificationEventKinds))
  .min(1, "notifications.rules.form.eventsRequired" satisfies MessageKey);

/** The rule form; an empty id means "any": no environment is the whole system, no stack the whole environment. */
export const ruleSchema = z.object({
  events: eventsSchema,
  environmentId: z.string(),
  stackId: z.string(),
});

export type RuleForm = z.infer<typeof ruleSchema>;

/** Changing an existing rule: only its events, the scope stays. */
export const ruleEventsSchema = z.object({ events: eventsSchema });

export type RuleEventsForm = z.infer<typeof ruleEventsSchema>;

export const emptyRuleForm: RuleForm = { events: ["DeploymentFailed"], environmentId: "", stackId: "" };

export function toCreateRuleRequest(form: RuleForm): CreateNotificationRuleRequest {
  return {
    events: form.events,
    environmentId: form.environmentId || null,
    // A stack only makes sense inside the chosen environment; the form clears it when the environment changes.
    stackId: form.environmentId && form.stackId ? form.stackId : null,
  };
}

/** Adds or removes one kind, keeping the aggregator's order so the list reads the same everywhere. */
export function toggleKind(events: readonly string[], kind: NotificationEventKind, on: boolean): NotificationEventKind[] {
  return notificationEventKinds.filter((k) => (k === kind ? on : events.includes(k)));
}

export function sameEvents(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((kind) => b.includes(kind));
}

export type RuleScope =
  | { kind: "system" }
  | { kind: "environment"; environment: string | null }
  | { kind: "stack"; environment: string | null; stack: string | null };

/** What a rule listens to; a name that is gone (a deleted stack) is null, and the screen says so. */
export function ruleScope(rule: NotificationRule): RuleScope {
  if (rule.stackId) {
    return { kind: "stack", environment: rule.environmentName, stack: rule.stackName };
  }

  if (rule.environmentId) {
    return { kind: "environment", environment: rule.environmentName };
  }

  return { kind: "system" };
}

/** How many records the history screen can load; the aggregator allows 1 to 500. */
export const historyLimits = [50, 100, 200, 500] as const;

export const DEFAULT_HISTORY_LIMIT = 100;

/** The history screen's URL: filters and how many records to load. */
export const historySearchSchema = z.object({
  status: z.enum(notificationStatuses).optional().catch(undefined),
  channel: z.string().optional().catch(undefined),
  limit: z.coerce
    .number()
    .refine((value) => (historyLimits as readonly number[]).includes(value))
    .optional()
    .catch(undefined),
});

export type HistorySearch = z.infer<typeof historySearchSchema>;

export function filterNotifications(
  notifications: readonly Notification[],
  { status, channel }: Pick<HistorySearch, "status" | "channel">,
): Notification[] {
  return notifications.filter(
    (n) => (status === undefined || n.status === status) && (channel === undefined || n.channelId === channel),
  );
}

/** Channels to filter the history by: today's channels, then deleted ones that still have records (name null). */
export function historyChannels(
  channels: readonly NotificationChannel[],
  notifications: readonly Notification[],
): { id: string; name: string | null }[] {
  const known = new Set(channels.map((c) => c.id));
  const deleted = [...new Set(notifications.filter((n) => !known.has(n.channelId)).map((n) => n.channelId))];
  return [...channels.map((c) => ({ id: c.id, name: c.name })), ...deleted.map((id) => ({ id, name: null }))];
}

/** Counts per status for the filter's labels. */
export function countByStatus(notifications: readonly Notification[]): Record<NotificationStatus, number> {
  const counts: Record<NotificationStatus, number> = { Pending: 0, Delivered: 0, Failed: 0, Expired: 0 };
  for (const n of notifications) {
    counts[n.status]++;
  }

  return counts;
}

/** While something waits for the bot, the history asks again this often; there is no live channel yet. */
export const PENDING_POLL_MS = 5_000;

export function hasPending(notifications: readonly Notification[]): boolean {
  return notifications.some((n) => n.status === "Pending");
}

/** The badge colour for a delivery status. */
export function statusVariant(status: NotificationStatus): "default" | "secondary" | "destructive" | "outline" {
  switch (status) {
    case "Delivered":
      return "default";
    case "Failed":
      return "destructive";
    case "Expired":
      return "outline";
    default:
      return "secondary";
  }
}

/** The badge colour for an event's severity. */
export function severityVariant(severity: NotificationSeverity): "secondary" | "outline" | "destructive" {
  switch (severity) {
    case "Error":
      return "destructive";
    case "Warning":
      return "outline";
    default:
      return "secondary";
  }
}

/**
 * A translated explanation for a failed request. The aggregator's messages are English, so known cases get the
 * panel's text by status; for a validation error its detail is shown too.
 */
export function describeError(error: unknown): { key: MessageKey; detail?: string } {
  if (!(error instanceof ApiError)) {
    return { key: "notifications.errors.network" };
  }

  switch (error.status) {
    case 400:
      return { key: "notifications.errors.invalid", detail: error.problem?.detail };
    case 403:
      return { key: "notifications.errors.forbidden" };
    case 404:
      return { key: "notifications.errors.notFound" };
    default:
      return { key: "notifications.errors.unexpected", detail: error.problem?.detail };
  }
}
