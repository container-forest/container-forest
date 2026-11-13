import type { components } from "@/api/schema.gen";
import type { AuditAction, AuditEntry, AuditQuery, AuditSearch } from "@/features/audit/model";

// Unmanaged stacks (ADR-0016): compose projects the host's administrator runs by hand. What may be done with one,
// without React. Hiding a button is cosmetic: the aggregator checks every action.

export type UnmanagedStack = components["schemas"]["UnmanagedStackResponse"];

export const unmanagedStackActions = ["up", "pull", "restart", "down"] as const;
export type UnmanagedStackAction = (typeof unmanagedStackActions)[number];

/** Whether the button for the action is shown at all: by the user's role, and never for a part of container-forest. */
export function isUnmanagedActionAllowed(action: UnmanagedStackAction, stack: UnmanagedStack): boolean {
  if (stack.isSystem) {
    return false;
  }

  return action === "restart" ? stack.canRestart : stack.canDeploy;
}

/** Restart and down have nothing to act on without containers; up and pull work from the compose file. */
export function isUnmanagedActionEnabled(action: UnmanagedStackAction, stack: UnmanagedStack): boolean {
  return action === "up" || action === "pull" || stack.hasContainers;
}

/** Interrupting the service is confirmed first; bringing it up and pulling images are not. */
export function needsUnmanagedConfirmation(action: UnmanagedStackAction): boolean {
  return action === "restart" || action === "down";
}

/** The compose files as the panel shows them: names relative to the project directory where they lie in it. */
export function shortConfigFiles(stack: UnmanagedStack): string[] {
  const prefix = stack.directory.endsWith("/") ? stack.directory : `${stack.directory}/`;
  return stack.configFiles.map((file) => (file.startsWith(prefix) ? file.slice(prefix.length) : file));
}

/** The stack's own page; the name is the compose project name, unique on its host. */
export function unmanagedStackLink(environmentId: string, name: string) {
  return { to: "/stacks/unmanaged/$environmentId/$stackName", params: { environmentId, stackName: name } } as const;
}

/** What the aggregator records for an unmanaged stack's compose operations. */
export const unmanagedStackAuditActions = [
  "UnmanagedStackStarted",
  "UnmanagedStackPulled",
  "UnmanagedStackRestarted",
  "UnmanagedStackTakenDown",
] as const satisfies readonly AuditAction[];

/** How many entries the stack's page shows; the audit log has the rest. */
export const UNMANAGED_HISTORY_SIZE = 5;

/**
 * The audit log's query for the stack's history. The log searches names by substring, so it is narrowed further by
 * {@link isUnmanagedStackEntry}; a page wider than what is shown leaves room for the entries of similar names.
 */
export function unmanagedHistoryQuery(environmentId: string, name: string): AuditQuery {
  return { environmentId, category: "Stacks", search: name, page: 1, pageSize: 50 };
}

/** The audit log's filters showing the same history in full. */
export function unmanagedHistorySearch(environmentId: string, name: string): AuditSearch {
  return { environment: environmentId, category: "Stacks", q: name };
}

/** An entry about this unmanaged stack: its name exactly, and no managed stack's id. */
export function isUnmanagedStackEntry(entry: AuditEntry, name: string): boolean {
  return (
    entry.targetKind === "Stack" &&
    entry.stackId === null &&
    entry.targetName === name &&
    (unmanagedStackAuditActions as readonly string[]).includes(entry.action)
  );
}
