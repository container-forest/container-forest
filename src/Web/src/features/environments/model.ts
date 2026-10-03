import agentComposeYaml from "@deploy/agent/compose.yaml?raw";
import { z } from "zod";
import { ApiError } from "@/api/client";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// What the environments and agent enrollment screens show and allow, without React: agent status, filters,
// forms, the agent's compose file, error texts.

export type Environment = components["schemas"]["EnvironmentResponse"];
export type RegistryCredential = components["schemas"]["RegistryCredentialResponse"];

/** Without live updates the agents' status is reloaded this often while a screen shows it; with them it is not polled. */
export const STATUS_REFRESH_MS = 15_000;

export const agentStatuses = ["online", "offline", "notEnrolled"] as const;
export type AgentStatus = (typeof agentStatuses)[number];

/** Online and offline are about an enrolled agent; an environment nobody has enrolled yet is neither. */
export function agentStatusOf(environment: Pick<Environment, "isEnrolled" | "isOnline">): AgentStatus {
  if (!environment.isEnrolled) {
    return "notEnrolled";
  }

  return environment.isOnline ? "online" : "offline";
}

export function statusVariant(status: AgentStatus): "default" | "secondary" | "outline" {
  switch (status) {
    case "online":
      return "default";
    case "offline":
      return "secondary";
    case "notEnrolled":
      return "outline";
  }
}

/** The environments screen's URL: the table filters, so a link shows the same view. */
export const environmentsSearchSchema = z.object({
  status: z.enum(agentStatuses).optional().catch(undefined),
  q: z.string().optional().catch(undefined),
});

export type EnvironmentsSearch = z.infer<typeof environmentsSearchSchema>;

/** Environments with the status asked for whose name or agent host contains the search, ignoring case. */
export function filterEnvironments(environments: readonly Environment[], search: EnvironmentsSearch): Environment[] {
  const text = search.q?.trim().toLowerCase() ?? "";
  return environments.filter(
    (e) =>
      (search.status === undefined || agentStatusOf(e) === search.status) &&
      (text === "" || e.name.toLowerCase().includes(text) || (e.agentHostname?.toLowerCase().includes(text) ?? false)),
  );
}

export type TokenState = "none" | "active" | "expired";

/**
 * Whether an issued enrollment token is still waiting for an agent. The aggregator forgets the token when an agent
 * enrolls with it, so "active" means a new agent can still enroll; only administrators get the expiry.
 */
export function tokenStateOf(environment: Pick<Environment, "enrollmentTokenExpiresAt">, now: Date): TokenState {
  if (!environment.enrollmentTokenExpiresAt) {
    return "none";
  }

  return new Date(environment.enrollmentTokenExpiresAt) > now ? "active" : "expired";
}

/** DockerEnvironment.MaxNameLength in the aggregator. */
export const MAX_ENVIRONMENT_NAME = 64;

// The aggregator trims the name and refuses control characters; it checks again and has the last word.
const controlCharacters = /[\u0000-\u001f\u007f]/;

export const environmentFormSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, "environments.form.nameInvalid" satisfies MessageKey)
    .max(MAX_ENVIRONMENT_NAME, "environments.form.nameInvalid" satisfies MessageKey)
    .refine((name) => !controlCharacters.test(name), "environments.form.nameInvalid" satisfies MessageKey),
  isPrivate: z.boolean(),
});

export type EnvironmentForm = z.infer<typeof environmentFormSchema>;

/** RegistryCredential.MaxUsernameLength in the aggregator. */
export const MAX_REGISTRY_USERNAME = 255;

// A host with an optional port, as `docker login` takes it; a scheme and a trailing "/" are tolerated, the
// aggregator normalizes them (and "docker.io" to Docker Hub's key).
const registryServerPattern = /^(https?:\/\/)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:\d{1,5})?\/?$/i;

export const registryCredentialSchema = z.object({
  server: z.string().trim().regex(registryServerPattern, "environments.registry.serverInvalid" satisfies MessageKey),
  username: z
    .string()
    .trim()
    .min(1, "environments.registry.usernameInvalid" satisfies MessageKey)
    .max(MAX_REGISTRY_USERNAME, "environments.registry.usernameInvalid" satisfies MessageKey)
    .refine((name) => !name.includes(":") && !controlCharacters.test(name), "environments.registry.usernameInvalid" satisfies MessageKey),
  // Not trimmed: a token is taken as typed.
  password: z.string().min(1, "environments.registry.passwordRequired" satisfies MessageKey),
});

export type RegistryCredentialForm = z.infer<typeof registryCredentialSchema>;

/** What the agent's .env file is filled from besides the token; not secret, editable on the screen. */
export interface AgentSettings {
  /** The aggregator's public name (CF_DOMAIN): the agent connects to https://<domain>. */
  domain: string;
  /** The name the panel shows for the host (AGENT_HOSTNAME). */
  hostname: string;
}

/**
 * A guess for the agent settings: the aggregator behind the same name as the panel (Caddy is the single entry point
 * and passes gRPC through, ADR-0005), and a host name from the environment's name.
 */
export function defaultAgentSettings(environmentName: string, panelHost: string): AgentSettings {
  return { domain: panelHost, hostname: hostnameFrom(environmentName) };
}

/** A valid host name label (RFC 1123) from free text: lowercase letters, digits and '-', at most 63 characters. */
export function hostnameFrom(name: string): string {
  const label = name
    .toLowerCase()
    .replace(/[^a-z0-9-]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 63)
    .replace(/-+$/, "");
  return label === "" ? "docker-host" : label;
}

const labelPattern = "[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?";
const domainPattern = new RegExp(`^${labelPattern}(\\.${labelPattern})*(:\\d{1,5})?$`, "i");
const hostnamePattern = new RegExp(`^${labelPattern}$`, "i");

/** A name with an optional port, without a scheme or path: it goes into https://${CF_DOMAIN}. */
export function isDomain(value: string): boolean {
  return value.length <= 253 && domainPattern.test(value.trim());
}

/** One RFC 1123 label, as Docker accepts for `hostname:`. */
export function isHostname(value: string): boolean {
  return hostnamePattern.test(value.trim());
}

/**
 * The agent's compose file: deploy/agent/compose.yaml itself, bundled at build time, so the screen shows exactly what
 * the repository documents. Everything specific to a host comes from {@link agentEnvFile}.
 */
export const AGENT_COMPOSE_FILE: string = agentComposeYaml;

/**
 * The .env file next to the compose file. It holds the enrollment token, so it stays on the host, readable by root
 * only, and is never committed; the token is needed for the first start only.
 */
export function agentEnvFile(settings: AgentSettings, enrollmentToken: string): string {
  return [
    `CF_DOMAIN=${settings.domain.trim()}`,
    `ENROLLMENT_TOKEN=${enrollmentToken}`,
    `AGENT_HOSTNAME=${settings.hostname.trim()}`,
    "",
  ].join("\n");
}

/**
 * A translated explanation for a failed request. The aggregator's messages are English, so known cases get the
 * panel's text by status; for a validation error and a conflict its detail is shown too.
 */
export function describeError(error: unknown): { key: MessageKey; detail?: string } {
  if (!(error instanceof ApiError)) {
    return { key: "environments.errors.network" };
  }

  switch (error.status) {
    case 400:
      return { key: "environments.errors.invalid", detail: error.problem?.detail };
    case 403:
      return { key: "environments.errors.forbidden" };
    case 404:
      return { key: "environments.errors.notFound" };
    case 409:
      return { key: "environments.errors.conflict", detail: error.problem?.detail };
    default:
      return { key: "environments.errors.unexpected", detail: error.problem?.detail };
  }
}
