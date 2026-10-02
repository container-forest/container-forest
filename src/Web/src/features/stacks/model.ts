import { z } from "zod";
import { ApiError } from "@/api/client";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// What the stack and deployment screens show and allow, without React: forms, states, error texts.

export type Stack = components["schemas"]["StackResponse"];
export type Repository = components["schemas"]["RepositoryResponse"];
export type Deployment = components["schemas"]["DeploymentResponse"];
export type DeploymentDetails = components["schemas"]["DeploymentDetailsResponse"];
export type DeploymentStatus = components["schemas"]["DeploymentStatus"];
export type DeploymentLogLine = components["schemas"]["DeploymentLogLineResponse"];
export type StackCommandResult = components["schemas"]["StackCommandResponse"];

export type StackAction = "restart" | "down";

/** While a deployment is unfinished its screen asks again this often; there is no live channel yet. */
export const DEPLOYMENT_POLL_MS = 2_000;

/** The stacks screen's URL: which environment is open. */
export const stacksSearchSchema = z.object({
  environment: z.string().optional().catch(undefined),
});

export type StacksSearch = z.infer<typeof stacksSearchSchema>;

// The aggregator's rules (StackName, StackFilePath) catch typos before sending; it checks everything again and
// has the last word. Branch names have too many git rules to copy: here only "one word", the rest is its 400.
export const stackNamePattern = /^[a-z0-9][a-z0-9_-]{0,62}$/;
const branchPattern = /^\S{1,255}$/;

/** A relative path inside the repository, without "." or ".." segments. */
export function isComposePath(value: string): boolean {
  if (value === "" || value.length > 255 || /[\\:]/.test(value)) {
    return false;
  }

  return value.split("/").every((segment) => segment !== "" && segment !== "." && segment !== "..");
}

const sourceFields = {
  repositoryId: z.string().min(1, "stacks.form.repositoryRequired" satisfies MessageKey),
  branch: z.string().trim().regex(branchPattern, "stacks.form.branchInvalid" satisfies MessageKey),
  composePath: z.string().trim().refine(isComposePath, "stacks.form.composePathInvalid" satisfies MessageKey),
};

export const stackSourceSchema = z.object(sourceFields);

export type StackSourceForm = z.infer<typeof stackSourceSchema>;

export const createStackSchema = z.object({
  name: z.string().trim().regex(stackNamePattern, "stacks.form.nameInvalid" satisfies MessageKey),
  ...sourceFields,
});

export type CreateStackForm = z.infer<typeof createStackSchema>;

/** GitRepository.MaxNameLength in the aggregator. */
export const MAX_REPOSITORY_NAME = 64;

export const createRepositorySchema = z.object({
  name: z.string().trim().min(1, "repositories.form.nameInvalid" satisfies MessageKey).max(MAX_REPOSITORY_NAME, "repositories.form.nameInvalid" satisfies MessageKey),
  // Only https: the aggregator fetches over HTTPS (LibGit2Sharp has no SSH).
  url: z.string().trim().regex(/^https:\/\/\S+$/i, "repositories.form.urlInvalid" satisfies MessageKey),
});

export type CreateRepositoryForm = z.infer<typeof createRepositorySchema>;

export function isFinished(status: DeploymentStatus): boolean {
  return status === "Succeeded" || status === "Failed" || status === "Cancelled";
}

/** The badge colour for a status. */
export function statusVariant(status: DeploymentStatus): "default" | "secondary" | "destructive" | "outline" {
  switch (status) {
    case "Succeeded":
      return "default";
    case "Failed":
      return "destructive";
    case "Cancelled":
      return "outline";
    default:
      return "secondary";
  }
}

/** The first 7 characters, as git shows a commit. */
export function shortCommit(sha: string | null | undefined): string | null {
  return sha ? sha.slice(0, 7) : null;
}

/** Whole seconds from start to finish (or to now while running); null before it started. */
export function durationSeconds(deployment: Pick<Deployment, "startedAt" | "finishedAt">, now: Date): number | null {
  if (!deployment.startedAt) {
    return null;
  }

  const end = deployment.finishedAt ? new Date(deployment.finishedAt) : now;
  return Math.max(0, Math.round((end.getTime() - new Date(deployment.startedAt).getTime()) / 1000));
}

/** "1:05" or "12 s" style is left to the screen; this splits seconds for the translation. */
export function splitDuration(seconds: number): { minutes: number; seconds: number } {
  return { minutes: Math.floor(seconds / 60), seconds: seconds % 60 };
}

/** A deployment can start only when none is running: the aggregator answers 409 otherwise. */
export function hasUnfinished(deployments: readonly Deployment[]): boolean {
  return deployments.some((d) => !isFinished(d.status));
}

/**
 * A translated explanation for a failed request. The aggregator's messages are English, so known cases get the
 * panel's text by status; for a conflict, a validation error and an agent error its detail is shown too.
 */
export function describeError(error: unknown): { key: MessageKey; detail?: string } {
  if (!(error instanceof ApiError)) {
    return { key: "stacks.errors.network" };
  }

  switch (error.status) {
    case 400:
      return { key: "stacks.errors.invalid", detail: error.problem?.detail };
    case 403:
      return { key: "stacks.errors.forbidden" };
    case 404:
      return { key: "stacks.errors.notFound" };
    case 409:
      return { key: "stacks.errors.conflict", detail: error.problem?.detail };
    case 502:
      return { key: "stacks.errors.agentFailed", detail: error.problem?.detail };
    case 503:
      return { key: "stacks.errors.agentOffline" };
    case 504:
      return { key: "stacks.errors.agentTimeout" };
    default:
      return { key: "stacks.errors.unexpected", detail: error.problem?.detail };
  }
}
