import { z } from "zod";
import { ApiError } from "@/api/client";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// What the containers screen shows and allows, without React: filters, actions per state, error texts.

export type Container = components["schemas"]["ContainerResponse"];
export type ContainerState = components["schemas"]["ContainerState"];
export type Environment = components["schemas"]["EnvironmentResponse"];

export type ContainerAction = "start" | "stop" | "restart" | "remove";

/** Filter values for the stack select: every container, or only those outside compose. */
export const ALL_STACKS = "all";
export const NO_STACK = "none";

export const stateFilters = ["all", "running", "stopped", "other"] as const;
export type StateFilter = (typeof stateFilters)[number];

/** The screen's URL: which environment, and the table filters, so a link shows the same view. */
export const containersSearchSchema = z.object({
  environment: z.string().optional().catch(undefined),
  stack: z.string().optional().catch(undefined),
  state: z.enum(stateFilters).optional().catch(undefined),
  q: z.string().optional().catch(undefined),
});

export type ContainersSearch = z.infer<typeof containersSearchSchema>;

/** Seconds Docker waits for the process to exit before killing it; the aggregator accepts 0..300 and defaults to 10. */
export const DEFAULT_STOP_TIMEOUT = 10;
export const MAX_STOP_TIMEOUT = 300;

export function stateFilterOf(state: ContainerState): Exclude<StateFilter, "all"> {
  switch (state) {
    case "Running":
    case "Restarting":
      return "running";
    case "Created":
    case "Exited":
    case "Dead":
      return "stopped";
    default:
      return "other";
  }
}

export function filterContainers(containers: readonly Container[], search: ContainersSearch): Container[] {
  const stack = search.stack ?? ALL_STACKS;
  const state = search.state ?? "all";
  const text = search.q?.trim().toLowerCase() ?? "";

  return containers
    .filter((c) => stack === ALL_STACKS || (stack === NO_STACK ? c.stackName === null : c.stackName === stack))
    .filter((c) => state === "all" || stateFilterOf(c.state) === state)
    .filter(
      (c) =>
        text === "" ||
        c.name.toLowerCase().includes(text) ||
        c.image.toLowerCase().includes(text) ||
        c.id.toLowerCase().startsWith(text),
    )
    .sort(compareContainers);
}

/** Grouped by stack as compose names them, containers outside compose last. */
export function compareContainers(a: Container, b: Container): number {
  if ((a.stackName === null) !== (b.stackName === null)) {
    return a.stackName === null ? 1 : -1;
  }

  return (a.stackName ?? "").localeCompare(b.stackName ?? "") || a.name.localeCompare(b.name);
}

/** The stacks present in the list, for the stack filter, and whether any container is outside compose. */
export function stackOptions(containers: readonly Container[]): { stacks: string[]; hasUnmanaged: boolean } {
  const stacks = new Set<string>();
  let hasUnmanaged = false;
  for (const container of containers) {
    if (container.stackName === null) {
      hasUnmanaged = true;
    } else {
      stacks.add(container.stackName);
    }
  }

  return { stacks: [...stacks].sort((a, b) => a.localeCompare(b)), hasUnmanaged };
}

/**
 * Whether the action makes sense in the container's state, by the same rules as the aggregator's commands.
 * An unknown state is left to Docker. Hiding a button is cosmetic: the aggregator checks every action.
 */
export function isActionAvailable(action: ContainerAction, container: Container): boolean {
  // Removing takes the Editor role, like taking a stack down; the rest Operator.
  if (action === "remove" ? !container.canRemove : !container.canControl) {
    return false;
  }

  // A part of container-forest itself (the aggregator, the panel, an agent) can only be started: stopping it
  // would cut the connection the commands travel over. The aggregator refuses the rest with 409.
  if (container.isSystem && action !== "start") {
    return false;
  }

  const state = container.state;
  if (state === "Unknown") {
    return true;
  }

  switch (action) {
    case "start":
      return state === "Created" || state === "Exited";
    case "stop":
      return state === "Running" || state === "Restarting" || state === "Paused";
    case "restart":
      return state !== "Removing" && state !== "Dead";
    case "remove":
      return state === "Created" || state === "Exited" || state === "Dead";
  }
}

/** Stopping, restarting and removing are confirmed; starting is not. */
export function needsConfirmation(action: ContainerAction): action is "stop" | "restart" | "remove" {
  return action !== "start";
}

/** Only actions that stop a process ask how long Docker may wait for it. */
export function takesStopTimeout(action: ContainerAction): action is "stop" | "restart" {
  return action === "stop" || action === "restart";
}

/** The typed stop timeout, or null when it is not a whole number of seconds the aggregator accepts. */
export function parseStopTimeout(value: string): number | null {
  const text = value.trim();
  if (!/^\d{1,3}$/.test(text)) {
    return null;
  }

  const seconds = Number(text);
  return seconds <= MAX_STOP_TIMEOUT ? seconds : null;
}

/**
 * A translated explanation for a failed request. The aggregator's own messages are English, so known cases get
 * the panel's text by status; for a conflict (state, ambiguous prefix) and an agent error its detail is shown too.
 */
export function describeError(error: unknown): { key: MessageKey; detail?: string } {
  if (!(error instanceof ApiError)) {
    return { key: "containers.errors.network" };
  }

  switch (error.status) {
    case 403:
      return { key: "containers.errors.forbidden" };
    case 404:
      return { key: "containers.errors.notFound" };
    case 409:
      return { key: "containers.errors.conflict", detail: error.problem?.detail };
    case 502:
      return { key: "containers.errors.agentFailed", detail: error.problem?.detail };
    case 503:
      return { key: "containers.errors.agentOffline" };
    case 504:
      return { key: "containers.errors.agentTimeout" };
    default:
      return { key: "containers.errors.unexpected", detail: error.problem?.detail };
  }
}
