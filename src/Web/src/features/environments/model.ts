import { z } from "zod";
import { ApiError } from "@/api/client";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// What the environments and agent enrollment screens show and allow, without React: agent status, filters,
// forms, the agent's compose file, error texts.

export type Environment = components["schemas"]["EnvironmentResponse"];
export type RegistryCredential = components["schemas"]["RegistryCredentialResponse"];

/** Without SignalR the agents' status is reloaded this often while a screen shows it. */
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

/** What the agent's compose file is built from; the token is added separately and never kept with these. */
export interface AgentSettings {
  image: string;
  controllerUrl: string;
  hostname: string;
}

/** Where the agent image will be published; the administrator replaces it with their registry's. */
export const DEFAULT_AGENT_IMAGE = "ghcr.io/<org>/container-forest-agent:latest";

/**
 * A guess for the agent settings: the aggregator behind the same address as the panel (a reverse proxy passes
 * gRPC through), and a host name from the environment's name. Both are editable on the screen.
 */
export function defaultAgentSettings(environmentName: string, panelOrigin: string): AgentSettings {
  return { image: DEFAULT_AGENT_IMAGE, controllerUrl: panelOrigin, hostname: hostnameFrom(environmentName) };
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

/** Whether the aggregator address is an absolute http(s) URL, which the agent requires. */
export function isControllerUrl(value: string): boolean {
  try {
    const url = new URL(value.trim());
    return (url.protocol === "https:" || url.protocol === "http:") && url.hostname !== "";
  } catch {
    return false;
  }
}

// Double-quoted YAML scalar: whatever is typed stays one value.
const yamlString = (value: string) => JSON.stringify(value);

/**
 * The agent's compose file for the host, as in docs/architecture.md ("Розгортання агента"). Directories are mounted
 * at the same path as on the host: compose inside the agent talks to the host's Docker, which resolves the paths.
 */
export function agentComposeFile(settings: AgentSettings, enrollmentToken: string): string {
  return [
    "services:",
    "  container-forest-agent:",
    `    image: ${yamlString(settings.image.trim())}`,
    "    restart: unless-stopped",
    `    hostname: ${yamlString(settings.hostname.trim())}`,
    "    environment:",
    `      CF_AGENT__CONTROLLERURL: ${yamlString(settings.controllerUrl.trim())}`,
    `      CF_AGENT__ENROLLMENTTOKEN: ${yamlString(enrollmentToken)}`,
    "    volumes:",
    "      - /var/run/docker.sock:/var/run/docker.sock",
    "      - /opt/container-forest:/opt/container-forest",
    "      - /run/container-forest:/run/container-forest",
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
