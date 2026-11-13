import { z } from "zod";
import type { components, operations } from "@/api/schema.gen";

// What the audit log screen shows and allows, without React: filters in the URL, the query they make, links to
// what an entry is about.

export type AuditEntry = components["schemas"]["AuditEntryResponse"];
export type AuditAction = components["schemas"]["AuditAction"];
export type AuditCategory = components["schemas"]["AuditCategory"];
export type AuditOutcome = components["schemas"]["AuditOutcome"];
export type AuditChannel = components["schemas"]["AuditChannel"];
export type AuditQuery = NonNullable<operations["ListAuditEntries"]["parameters"]["query"]>;

export const auditCategories = [
  "Account",
  "Users",
  "Tokens",
  "Environments",
  "Stacks",
  "Deployments",
  "Containers",
  "Secrets",
  "System",
] as const satisfies readonly AuditCategory[];

export const auditOutcomes = ["Succeeded", "Denied", "Failed"] as const satisfies readonly AuditOutcome[];

/** Every action the aggregator records, in its order; used to tell known ones from those of a newer aggregator. */
export const auditActions = [
  "SignedIn",
  "SignInFailed",
  "SignedOut",
  "PasswordChanged",
  "DiscordLinked",
  "DiscordUnlinked",
  "UserCreated",
  "UserRenamed",
  "UserDeactivated",
  "UserActivated",
  "AdminGranted",
  "AdminRevoked",
  "PasswordReset",
  "UserUnlocked",
  "RoleGranted",
  "RoleChanged",
  "RoleRevoked",
  "ApiTokenCreated",
  "ApiTokenRevoked",
  "EnvironmentCreated",
  "EnvironmentUpdated",
  "EnvironmentRemoved",
  "EnrollmentTokenIssued",
  "AgentRevoked",
  "StackCreated",
  "StackRemoved",
  "StackSourceChanged",
  "DeployHookCreated",
  "DeployHookRemoved",
  "ImagePolicyAdded",
  "ImagePolicyChanged",
  "ImagePolicyRemoved",
  "ImagePinned",
  "ImageUnpinned",
  "ImageHookCreated",
  "ImageHookRemoved",
  "StackRestarted",
  "StackTakenDown",
  "UnmanagedStackStarted",
  "UnmanagedStackPulled",
  "UnmanagedStackRestarted",
  "UnmanagedStackTakenDown",
  "DeploymentQueued",
  "ContainerStarted",
  "ContainerStopped",
  "ContainerRestarted",
  "ContainerRemoved",
  "SecretsDecrypted",
  "IdentityConfirmed",
  "SettingsChanged",
  "BackupRequested",
  "GitPushCredentialChanged",
  "AggregatorSigningKeyRotated",
] as const satisfies readonly AuditAction[];

export function isKnownAction(action: string): action is (typeof auditActions)[number] {
  return (auditActions as readonly string[]).includes(action);
}

export const auditChannels = ["Panel", "ApiToken", "Bot", "Anonymous"] as const satisfies readonly AuditChannel[];

export function isKnownChannel(channel: string): channel is (typeof auditChannels)[number] {
  return (auditChannels as readonly string[]).includes(channel);
}

export const AUDIT_PAGE_SIZE = 50;

/** A calendar day as the date input gives it. */
const dayPattern = /^\d{4}-\d{2}-\d{2}$/;

const guid = z.string().regex(/^[0-9a-f-]{36}$/i);

export const auditSearchSchema = z.object({
  category: z.enum(auditCategories).optional().catch(undefined),
  outcome: z.enum(auditOutcomes).optional().catch(undefined),
  user: guid.optional().catch(undefined),
  environment: guid.optional().catch(undefined),
  q: z.string().trim().min(1).max(100).optional().catch(undefined),
  /** First day shown, inclusive. */
  from: z.string().regex(dayPattern).optional().catch(undefined),
  /** Last day shown, inclusive. */
  to: z.string().regex(dayPattern).optional().catch(undefined),
  page: z.coerce.number().int().min(2).optional().catch(undefined),
});

export type AuditSearch = z.infer<typeof auditSearchSchema>;

/** Whether any filter narrows the log; the page alone does not count. */
export function isFiltered(search: AuditSearch): boolean {
  return [search.category, search.outcome, search.user, search.environment, search.q, search.from, search.to].some(
    (value) => value !== undefined,
  );
}

/**
 * The aggregator's query for the filters. Days are the viewer's local days: "to" covers its whole day, so it becomes
 * the start of the next one, which the aggregator treats as exclusive.
 */
export function auditQueryOf(search: AuditSearch): AuditQuery {
  return {
    category: search.category,
    outcome: search.outcome,
    userId: search.user,
    environmentId: search.environment,
    search: search.q,
    from: search.from ? startOfLocalDay(search.from, 0) : undefined,
    to: search.to ? startOfLocalDay(search.to, 1) : undefined,
    page: search.page ?? 1,
    pageSize: AUDIT_PAGE_SIZE,
  };
}

function startOfLocalDay(day: string, plusDays: number): string {
  // The schema already checked the yyyy-mm-dd shape.
  const [year = 1970, month = 1, date = 1] = day.split("-").map(Number);
  return new Date(year, month - 1, date + plusDays).toISOString();
}

/** "51–100" of the total, and whether there are pages before and after. */
export function pageRange(page: number, pageSize: number, total: number) {
  const first = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const last = Math.min(page * pageSize, total);
  return { first, last, hasPrevious: page > 1, hasNext: last < total };
}

/**
 * Where the entry's target lives in the panel, if it still can: a removed object's page answers "not found", which
 * is fine for a log. Files, tokens and failed sign-ins of unknown names have no page.
 */
export type TargetLink =
  | { to: "/users/$userId"; params: { userId: string } }
  | { to: "/environments/$environmentId"; params: { environmentId: string } }
  | { to: "/stacks/$stackId"; params: { stackId: string } }
  | { to: "/stacks/unmanaged/$environmentId/$stackName"; params: { environmentId: string; stackName: string } }
  | { to: "/containers/$environmentId/$containerId/logs"; params: { environmentId: string; containerId: string } };

export function targetLink(entry: AuditEntry): TargetLink | undefined {
  switch (entry.targetKind) {
    case "User":
      return entry.targetId && looksLikeGuid(entry.targetId)
        ? { to: "/users/$userId", params: { userId: entry.targetId } }
        : undefined;
    case "Environment":
      return entry.environmentId ? { to: "/environments/$environmentId", params: { environmentId: entry.environmentId } } : undefined;
    case "Stack":
      if (entry.stackId) {
        return { to: "/stacks/$stackId", params: { stackId: entry.stackId } };
      }

      // An unmanaged stack (ADR-0016) has no id; the compose project name on its environment is its address.
      return entry.environmentId && entry.targetName && entry.action.startsWith("UnmanagedStack")
        ? { to: "/stacks/unmanaged/$environmentId/$stackName", params: { environmentId: entry.environmentId, stackName: entry.targetName } }
        : undefined;
    case "Container":
      return entry.environmentId && entry.targetName
        ? { to: "/containers/$environmentId/$containerId/logs", params: { environmentId: entry.environmentId, containerId: entry.targetName } }
        : undefined;
    default:
      return undefined;
  }
}

function looksLikeGuid(value: string): boolean {
  return guid.safeParse(value).success;
}

export function outcomeVariant(outcome: AuditOutcome): "secondary" | "destructive" | "outline" {
  switch (outcome) {
    case "Succeeded":
      return "secondary";
    case "Denied":
      return "destructive";
    default:
      return "outline";
  }
}
