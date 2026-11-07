import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// Signing keys without React (ADR-0011): the form, the request it becomes and how a key's scope is shown.

export type SigningKey = components["schemas"]["SigningKeyResponse"];
export type AddSigningKeyRequest = components["schemas"]["AddSigningKeyRequest"];
export type UpdateSigningKeyRequest = components["schemas"]["UpdateSigningKeyRequest"];

/** SigningKey.MaxNameLength in the aggregator. */
export const MAX_SIGNING_KEY_NAME = 64;

const name = z
  .string()
  .trim()
  .min(1, "signingKeys.form.nameRequired" satisfies MessageKey)
  .max(MAX_SIGNING_KEY_NAME, "signingKeys.form.nameTooLong" satisfies MessageKey);

/**
 * One form for adding and editing a key. Editing changes only the name and the environments; it starts from the
 * key's own public key, so the same checks pass.
 */
export const signingKeySchema = z
  .object({
    name,
    // The aggregator checks the key itself; here only that something was pasted.
    publicKey: z.string().trim().min(1, "signingKeys.form.publicKeyRequired" satisfies MessageKey),
    allEnvironments: z.boolean(),
    environmentIds: z.array(z.string()),
  })
  .refine((form) => form.allEnvironments || form.environmentIds.length > 0, {
    path: ["environmentIds"],
    message: "signingKeys.form.environmentsRequired" satisfies MessageKey,
  });

export type SigningKeyForm = z.infer<typeof signingKeySchema>;

export const emptySigningKeyForm: SigningKeyForm = { name: "", publicKey: "", allEnvironments: true, environmentIds: [] };

export function toAddSigningKeyRequest(form: SigningKeyForm): AddSigningKeyRequest {
  return { name: form.name.trim(), publicKey: form.publicKey.trim(), environmentIds: form.allEnvironments ? null : form.environmentIds };
}

export function toUpdateSigningKeyRequest(form: SigningKeyForm): UpdateSigningKeyRequest {
  return { name: form.name.trim(), environmentIds: form.allEnvironments ? null : form.environmentIds };
}

export function editFormOf(key: SigningKey): SigningKeyForm {
  return {
    name: key.name,
    publicKey: key.publicKey,
    allEnvironments: key.environmentIds == null,
    environmentIds: key.environmentIds ?? [],
  };
}

/** Names of the environments a limited key endorses, in the order of `environments`; unknown ids are left out. */
export function scopeNames(key: SigningKey, environments: readonly { id: string; name: string }[]): string[] | null {
  if (key.environmentIds == null) {
    return null;
  }

  const ids = new Set(key.environmentIds);
  return environments.filter((environment) => ids.has(environment.id)).map((environment) => environment.name);
}
