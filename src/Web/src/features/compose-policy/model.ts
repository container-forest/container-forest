import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

export type ComposePolicy = components["schemas"]["ComposePolicyResponse"];
export type ComposePolicyMode = components["schemas"]["ComposePolicyMode"];
export type StackPolicyReport = components["schemas"]["StackPolicyReportResponse"];

export const policyModes = ["Off", "Audit", "Enforce"] as const satisfies readonly ComposePolicyMode[];

/** One entry per line: blank lines and duplicates are dropped, surrounding spaces trimmed. */
export function parseLines(text: string): string[] {
  return [...new Set(text.split("\n").map((line) => line.trim()).filter((line) => line.length > 0))];
}

export const joinLines = (values: readonly string[]) => values.join("\n");

/** Absolute paths without "." or ".." segments, as the aggregator accepts them. */
export function isHostPath(path: string): boolean {
  return path.startsWith("/") && !path.split("/").some((segment) => segment === "." || segment === "..");
}

export const policySchema = z.object({
  mode: z.enum(policyModes),
  dataRoot: z.string().trim().refine(isHostPath, "composePolicy.errors.path" satisfies MessageKey),
  systemReadOnlyPaths: z
    .string()
    .refine((text) => parseLines(text).every(isHostPath), "composePolicy.errors.path" satisfies MessageKey),
});

export type PolicyForm = z.infer<typeof policySchema>;

export function formOf(policy: ComposePolicy): PolicyForm {
  return {
    mode: policy.mode,
    dataRoot: policy.dataRoot,
    systemReadOnlyPaths: joinLines(policy.systemReadOnlyPaths),
  };
}

/** Stacks of the report that would fail a deployment under Enforce, first. */
export function sortReport(report: readonly StackPolicyReport[]): StackPolicyReport[] {
  return [...report].sort((a, b) => b.violations.length - a.violations.length || a.stackName.localeCompare(b.stackName));
}

/** What a kind of allowance takes after ':' in the panel's form; "flag" takes nothing. */
export type AllowanceInput = "flag" | "namespace" | "capability" | "port" | "path" | "device" | "name" | "label" | "option" | "key";

/**
 * The kinds of allowance the panel offers, in the order of its list (PolicyAllowance in the aggregator). `path` and
 * `path-rw` are one kind in the form, with the access as a choice. `root` marks those that give the stack root on the
 * host whatever else the policy says: the form asks to confirm them.
 */
export const allowanceKinds = [
  { kind: "privileged", input: "flag", root: true },
  { kind: "docker-socket", input: "flag", root: true },
  { kind: "host", input: "namespace", root: true },
  { kind: "capability", input: "capability", root: true },
  { kind: "path", input: "path", root: false },
  { kind: "port", input: "port", root: false },
  { kind: "network", input: "name", root: false },
  { kind: "device", input: "device", root: false },
  { kind: "volume", input: "name", root: false },
  { kind: "network-driver", input: "name", root: false },
  { kind: "volume-driver", input: "flag", root: true },
  { kind: "security-opt", input: "option", root: true },
  { kind: "gpus", input: "flag", root: false },
  { kind: "device-cgroup-rules", input: "flag", root: true },
  { kind: "cgroup-parent", input: "flag", root: false },
  { kind: "runtime", input: "name", root: true },
  { kind: "provider", input: "flag", root: true },
  { kind: "foreign-container", input: "flag", root: false },
  { kind: "propagation", input: "flag", root: true },
  { kind: "label", input: "label", root: false },
  { kind: "container-name", input: "flag", root: false },
  { kind: "key", input: "key", root: false },
] as const satisfies readonly { kind: string; input: AllowanceInput; root: boolean }[];

export type AllowanceKind = (typeof allowanceKinds)[number]["kind"];

export const hostNamespaces = ["network", "pid", "ipc", "uts", "userns", "cgroup"] as const;

/** Capabilities people add most; any other name can be typed. */
export const commonCapabilities = ["SYS_ADMIN", "SYS_PTRACE", "SYS_TIME", "SYS_MODULE", "DAC_READ_SEARCH", "BPF", "PERFMON"] as const;

/** An allowance split for display: its kind in the form (`path-rw` shows as `path`), its value, and write access. */
export interface ParsedAllowance {
  kind: AllowanceKind;
  value: string | null;
  writable: boolean;
}

export function parseAllowance(text: string): ParsedAllowance | undefined {
  const colon = text.indexOf(":");
  const rawKind = colon < 0 ? text : text.slice(0, colon);
  const value = colon < 0 ? null : text.slice(colon + 1);
  const kind = rawKind === "path-rw" ? "path" : rawKind;
  return allowanceKinds.some((k) => k.kind === kind) ? { kind: kind as AllowanceKind, value, writable: rawKind === "path-rw" } : undefined;
}

export function formatAllowance(kind: AllowanceKind, value: string | null, writable = false): string {
  const name = kind === "path" && writable ? "path-rw" : kind;
  return value === null ? name : `${name}:${value}`;
}

export function kindOf(kind: AllowanceKind) {
  return allowanceKinds.find((k) => k.kind === kind)!;
}

/** Whether an allowance gives the stack root on the host: a "root" kind, or a path the policy protects. */
export function givesRoot(allowance: string, protectedPaths: readonly string[]): boolean {
  const parsed = parseAllowance(allowance);
  if (!parsed) return false;
  if (kindOf(parsed.kind).root) return true;
  return parsed.kind === "path" && parsed.value !== null && isProtected(parsed.value, protectedPaths);
}

/** As the aggregator: the path is protected, lies under a protected path or above one ("/" only itself). */
export function isProtected(path: string, protectedPaths: readonly string[]): boolean {
  const under = (p: string, dir: string) => p === dir || p.startsWith(`${dir}/`);
  return protectedPaths.some((p) => (p === "/" ? path === "/" : under(path, p) || under(p, path)));
}

/** Trailing slashes off, as the aggregator normalizes paths. */
export function normalizePath(path: string): string {
  const trimmed = path.trim();
  return trimmed === "/" ? trimmed : trimmed.replace(/\/+$/, "");
}

/** Checks the value typed for a kind; returns the translation key of the problem, or undefined when it is fine. */
export function valueProblem(input: AllowanceInput, value: string): MessageKey | undefined {
  const v = value.trim();
  switch (input) {
    case "flag":
    case "namespace":
      return undefined;
    case "port": {
      const port = Number(v);
      return Number.isInteger(port) && port > 0 && port < 1024 && String(port) === v ? undefined : "allowances.errors.port";
    }
    case "path":
    case "device":
      return isHostPath(normalizePath(v)) && v.length > 0 ? undefined : "allowances.errors.path";
    case "capability":
      return /^(CAP_)?[A-Za-z][A-Za-z0-9_]{0,31}$/.test(v) ? undefined : "allowances.errors.capability";
    case "name":
      return /^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$/.test(v) ? undefined : "allowances.errors.name";
    case "label":
      return /^[A-Za-z0-9][A-Za-z0-9_./-]{0,254}$/.test(v) ? undefined : "allowances.errors.name";
    case "key":
      return /^[a-z][a-z0-9_]{0,63}$/.test(v) ? undefined : "allowances.errors.key";
    case "option":
      return v.length > 0 && !/\s/.test(v) ? undefined : "allowances.errors.option";
  }
}

/** The value as the aggregator stores it: capability names in upper case without CAP_, paths without trailing slashes. */
export function normalizeValue(input: AllowanceInput, value: string): string {
  const v = value.trim();
  if (input === "capability") return v.toUpperCase().replace(/^CAP_/, "");
  if (input === "path" || input === "device") return normalizePath(v);
  return v;
}
