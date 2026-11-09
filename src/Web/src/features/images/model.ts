import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

export type ImagePolicy = components["schemas"]["ImagePolicyResponse"];
export type ImageUpdateRecord = components["schemas"]["ImageUpdateRecordResponse"];
export type ImageUpdateResult = components["schemas"]["ImageUpdateResultResponse"];
export type ImageTagOrder = components["schemas"]["ImageTagOrder"];
export type ImageUpdateOutcome = components["schemas"]["ImageUpdateOutcome"];
export type AggregatorSigningKey = components["schemas"]["AggregatorSigningKeyResponse"];

/** The pattern most CI tags follow: the branch and the short commit, e.g. main-7822307. */
export const DEFAULT_TAG_PATTERN = "^main-[0-9a-f]{7}\\z";

/** The form of a policy; services are typed as a comma-separated list. */
export const imagePolicySchema = z
  .object({
    image: z.string().trim().min(1, { message: "images.form.errors.image" satisfies MessageKey }),
    tagPattern: z
      .string()
      .trim()
      .refine((value) => value.startsWith("^") && value.endsWith("\\z"), { message: "images.form.errors.anchors" satisfies MessageKey }),
    order: z.enum(["ImageCreated", "TagTimestamp"]),
    services: z.string(),
    poll: z.boolean(),
    acceptRequests: z.boolean(),
    pinDigest: z.boolean(),
  })
  .refine((form) => form.order !== "TagTimestamp" || form.tagPattern.includes("(?<ts>"), {
    message: "images.form.errors.timestamp" satisfies MessageKey,
    path: ["tagPattern"],
  });

export type ImagePolicyForm = z.infer<typeof imagePolicySchema>;

export function emptyPolicyForm(): ImagePolicyForm {
  return { image: "", tagPattern: DEFAULT_TAG_PATTERN, order: "ImageCreated", services: "", poll: true, acceptRequests: true, pinDigest: false };
}

export function policyForm(policy: ImagePolicy): ImagePolicyForm {
  return {
    image: policy.image,
    tagPattern: policy.tagPattern,
    order: policy.order,
    services: policy.services.join(", "),
    poll: policy.poll,
    acceptRequests: policy.acceptRequests,
    pinDigest: policy.pinDigest,
  };
}

/** The settings part of the form, as the API takes it. */
export function toSettings(form: ImagePolicyForm) {
  return {
    tagPattern: form.tagPattern,
    order: form.order,
    services: parseServices(form.services),
    poll: form.poll,
    acceptRequests: form.acceptRequests,
    pinDigest: form.pinDigest,
  };
}

export function parseServices(text: string): string[] {
  return [...new Set(text.split(/[\s,]+/).filter((name) => name.length > 0))];
}

/** Where CI calls the stack's image hook. */
export function imageHookUrl(origin: string, stackId: string): string {
  return `${origin.replace(/\/+$/, "")}/api/image-hooks/${stackId}`;
}

/** The call CI makes after pushing an image; the token is read from a CI secret, never written into the command. */
export function imageHookCommand(url: string, image: string, tokenVariable = "FOREST_IMAGE_HOOK"): string {
  return `curl -fsS -X POST -H "Authorization: Bearer $${tokenVariable}" -H 'Content-Type: application/json' -d '{"image":"${image}"}' ${url}`;
}

/** The state of a policy in one word, for its badge. */
export function policyState(policy: ImagePolicy): "pinned" | "failed" | "unchecked" | "ok" {
  if (policy.pinnedTag) {
    return "pinned";
  }

  if (policy.lastError) {
    return "failed";
  }

  return policy.lastCheckedAt ? "ok" : "unchecked";
}

/** What the outcome of a check means for its message. */
export function outcomeKey(outcome: ImageUpdateOutcome): MessageKey {
  return `images.outcome.${outcome}` as const satisfies MessageKey;
}
