import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

export type ComposePolicy = components["schemas"]["ComposePolicyResponse"];
export type ComposePolicyMode = components["schemas"]["ComposePolicyMode"];
export type StackPolicyReport = components["schemas"]["StackPolicyReportResponse"];

export const policyModes = ["Off", "Audit", "Enforce"] as const satisfies readonly ComposePolicyMode[];

// Allowances that take no value, and the kinds that take one after ':' (PolicyAllowance in the aggregator).
const flags = [
  "privileged",
  "docker-socket",
  "gpus",
  "device-cgroup-rules",
  "cgroup-parent",
  "provider",
  "volume-driver",
  "foreign-container",
  "propagation",
  "container-name",
];
const kinds = [
  "host",
  "capability",
  "security-opt",
  "device",
  "runtime",
  "path",
  "path-rw",
  "port",
  "network",
  "network-driver",
  "volume",
  "label",
  "key",
];

/** A first check of an allowance; the aggregator validates it fully and says why it refuses one. */
export function looksLikeAllowance(text: string): boolean {
  if (flags.includes(text)) return true;
  const colon = text.indexOf(":");
  return colon > 0 && colon < text.length - 1 && kinds.includes(text.slice(0, colon));
}

/** One entry per line: blank lines and duplicates are dropped, surrounding spaces trimmed. */
export function parseLines(text: string): string[] {
  return [...new Set(text.split("\n").map((line) => line.trim()).filter((line) => line.length > 0))];
}

export const joinLines = (values: readonly string[]) => values.join("\n");

/** Absolute paths without "." or ".." segments, as the aggregator accepts them. */
export function isHostPath(path: string): boolean {
  return path.startsWith("/") && !path.split("/").some((segment) => segment === "." || segment === "..");
}

const allowancesField = z
  .string()
  .refine((text) => parseLines(text).every(looksLikeAllowance), "composePolicy.errors.allowance" satisfies MessageKey);

export const policySchema = z.object({
  mode: z.enum(policyModes),
  dataRoot: z.string().trim().refine(isHostPath, "composePolicy.errors.path" satisfies MessageKey),
  systemReadOnlyPaths: z
    .string()
    .refine((text) => parseLines(text).every(isHostPath), "composePolicy.errors.path" satisfies MessageKey),
  allowances: allowancesField,
});

export type PolicyForm = z.infer<typeof policySchema>;

export const allowancesSchema = z.object({ allowances: allowancesField });

export type AllowancesForm = z.infer<typeof allowancesSchema>;

export function formOf(policy: ComposePolicy): PolicyForm {
  return {
    mode: policy.mode,
    dataRoot: policy.dataRoot,
    systemReadOnlyPaths: joinLines(policy.systemReadOnlyPaths),
    allowances: joinLines(policy.allowances),
  };
}

/** Stacks of the report that would fail a deployment under Enforce, first. */
export function sortReport(report: readonly StackPolicyReport[]): StackPolicyReport[] {
  return [...report].sort((a, b) => b.violations.length - a.violations.length || a.stackName.localeCompare(b.stackName));
}
