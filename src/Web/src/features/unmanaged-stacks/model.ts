import type { components } from "@/api/schema.gen";

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
