import { z } from "zod";
import type { components } from "@/api/schema.gen";

export type AgeKey = components["schemas"]["AgeKeyResponse"];

export const keysSearchSchema = z.object({
  environment: z.string().optional().catch(undefined),
});

/** The aggregator lists keys newest first; files are encrypted for the newest, older ones still decrypt. */
export function currentKey(keys: readonly AgeKey[], stackId: string | null = null): AgeKey | undefined {
  return keys.find((key) => (key.stackId ?? null) === stackId);
}

/** Whether a key is the newest of its scope: the environment's own, or one stack's (ADR-0012). */
export function isCurrent(keys: readonly AgeKey[], key: AgeKey): boolean {
  return currentKey(keys, key.stackId ?? null)?.id === key.id;
}

/**
 * A `.sops.yaml` rule for the stacks of one environment. Next to the environment's key it leaves a place for the
 * user's own: the aggregator's key only decrypts on the aggregator, and without one of their own nobody could edit
 * the file again.
 */
export function sopsConfig(environmentName: string, recipient: string): string {
  return [
    "creation_rules:",
    `  # Stacks of environment "${environmentName}": point path_regex at their directory.`,
    `  - path_regex: ^${regexEscape(slug(environmentName))}/`,
    "    age: >-",
    `      ${recipient},`,
    "      <your own age public key>",
    "",
  ].join("\n");
}

/** The commands for the panel's how-to; the file names are examples. */
export const sopsCommands = {
  encrypt: "sops encrypt --in-place secrets.env",
  edit: "sops edit secrets.env",
  updateKeys: "sops updatekeys secrets.env",
} as const;

function slug(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9._-]+/g, "-") || "environment";
}

function regexEscape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
