import type { RoleAssignment, User } from "@/api/users";

export const userStatuses = ["active", "locked", "deactivated"] as const;

export type UserStatus = (typeof userStatuses)[number];

export const userStatusFilters = ["all", ...userStatuses] as const;

export type UserStatusFilter = (typeof userStatusFilters)[number];

/** A deactivated user has no access at all, so that wins over a lockout. */
export function userStatus(user: User, now: Date): UserStatus {
  if (!user.isActive) {
    return "deactivated";
  }

  return user.lockedOutUntil !== null && new Date(user.lockedOutUntil) > now ? "locked" : "active";
}

/** Users whose name contains the query (any case) and whose status matches the filter. */
export function filterUsers(users: readonly User[], query: string, status: UserStatusFilter, now: Date): User[] {
  const needle = query.trim().toLocaleLowerCase();
  return users.filter(
    (user) =>
      (needle === "" || user.userName.toLocaleLowerCase().includes(needle)) &&
      (status === "all" || userStatus(user, now) === status),
  );
}

/** The same rule as the aggregator's User: 3-64 letters, digits, '.', '_' or '-'. */
export const userNamePattern = /^[A-Za-z0-9._-]{3,64}$/;

/**
 * Scopes where the user already has a role: a second role there is a conflict, so the grant form offers them
 * disabled and the existing role is changed in the table instead.
 */
export function takenScopes(assignments: readonly RoleAssignment[]) {
  return {
    environments: new Set(assignments.filter((a) => a.stackId === null).map((a) => a.environmentId)),
    stacks: new Set(assignments.flatMap((a) => (a.stackId === null ? [] : [a.stackId]))),
  };
}
