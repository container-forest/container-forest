import { z } from "zod";
import type { AuditSearch } from "@/features/audit/model";
import type { ContainersSearch } from "@/features/containers/model";
import type { EnvironmentsSearch } from "@/features/environments/model";
import type { StacksSearch } from "@/features/stacks/model";
import type { UserStatusFilter } from "@/lib/users";

/**
 * Where a detail page was opened from when that isn't its own list, so its back link returns there. It travels in the
 * `from` search param rather than history state, so it survives a reload and a link opened in a new tab.
 *
 * - `stack:<id>`: a managed stack's page (its containers, its deployments);
 * - `unmanaged:<name>`: an unmanaged stack's page in the same environment as the container (ADR-0016);
 * - `audit`: the audit log.
 */
export type Origin = { kind: "stack"; stackId: string } | { kind: "unmanaged"; stackName: string } | { kind: "audit" };

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
// The compose project name rule (`^[a-z0-9][a-z0-9_-]*`, up to 63 characters). JavaScript's `$` without the `m` flag
// matches only at the end of the input, unlike .NET's.
const stackNamePattern = /^[a-z0-9][a-z0-9_-]{0,62}$/;

export function parseOrigin(value: string | undefined): Origin | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (value === "audit") {
    return { kind: "audit" };
  }

  const separator = value.indexOf(":");
  const kind = value.slice(0, separator);
  const rest = value.slice(separator + 1);
  if (separator > 0 && kind === "stack" && guidPattern.test(rest)) {
    return { kind: "stack", stackId: rest };
  }
  if (separator > 0 && kind === "unmanaged" && stackNamePattern.test(rest)) {
    return { kind: "unmanaged", stackName: rest };
  }
  return undefined;
}

export function originParam(origin: Origin): string {
  switch (origin.kind) {
    case "stack":
      return `stack:${origin.stackId}`;
    case "unmanaged":
      return `unmanaged:${origin.stackName}`;
    case "audit":
      return "audit";
  }
}

/** The `from` search param of a detail page; anything that isn't a known origin is dropped. */
export const fromSearchSchema = z
  .string()
  .refine((value) => parseOrigin(value) !== undefined)
  .optional()
  .catch(undefined);

/**
 * The last filters of each list screen in this tab, so a back link from a detail page restores them. Memory only:
 * a new tab or a reload starts from the defaults, as the list itself would.
 */
interface ListSearches {
  containers: ContainersSearch;
  stacks: StacksSearch;
  audit: AuditSearch;
  users: { q?: string; status?: UserStatusFilter };
  environments: EnvironmentsSearch;
}

const remembered = new Map<keyof ListSearches, unknown>();

export function rememberListSearch<K extends keyof ListSearches>(list: K, search: ListSearches[K]): void {
  remembered.set(list, search);
}

export function rememberedListSearch<K extends keyof ListSearches>(list: K): ListSearches[K] | undefined {
  return remembered.get(list) as ListSearches[K] | undefined;
}

/**
 * The search of a list filtered by environment (containers, stacks) for a back link from a page in `environmentId`:
 * the remembered filters when they were for that environment, else just the environment.
 */
export function listSearchFor<K extends "containers" | "stacks">(list: K, environmentId: string): ListSearches[K] {
  const last = rememberedListSearch(list);
  return last?.environment === environmentId ? last : ({ environment: environmentId } as ListSearches[K]);
}

/** Where a back link can lead: a detail page's list (with its remembered filters) or one of the origins. */
export type BackLinkTarget =
  | { to: "/stacks/$stackId"; params: { stackId: string } }
  | { to: "/stacks/unmanaged/$environmentId/$stackName"; params: { environmentId: string; stackName: string } }
  | { to: "/stacks/$stackId/deployments"; params: { stackId: string } }
  | { to: "/audit"; search: AuditSearch }
  | { to: "/containers"; search: ContainersSearch }
  | { to: "/stacks"; search: StacksSearch }
  | { to: "/users"; search: ListSearches["users"] }
  | { to: "/environments"; search: EnvironmentsSearch };

/**
 * The page an origin names. An unmanaged stack is addressed within an environment, so it needs the page's one;
 * a managed stack's id must be `stackId` when the page belongs to one stack (a deployment), else it is ignored.
 */
export function originLink(origin: Origin | undefined, page: { environmentId?: string; stackId?: string }): BackLinkTarget | undefined {
  switch (origin?.kind) {
    case "stack":
      return page.stackId === undefined || page.stackId === origin.stackId
        ? { to: "/stacks/$stackId", params: { stackId: origin.stackId } }
        : undefined;
    case "unmanaged":
      return page.environmentId === undefined
        ? undefined
        : { to: "/stacks/unmanaged/$environmentId/$stackName", params: { environmentId: page.environmentId, stackName: origin.stackName } };
    case "audit":
      return { to: "/audit", search: rememberedListSearch("audit") ?? {} };
    case undefined:
      return undefined;
  }
}
