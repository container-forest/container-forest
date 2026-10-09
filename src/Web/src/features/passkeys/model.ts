import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// Passkeys (WebAuthn, ADR-0009) without React: the browser's side of the ceremonies and what their errors mean.
// The aggregator's options are WebAuthn JSON; the browser parses them itself (parse*OptionsFromJSON) and serializes
// its answer with toJSON(), so no WebAuthn library is needed.

export type Passkey = components["schemas"]["PasskeyResponse"];

/** PasskeyManagement.MaxNameLength in the aggregator. */
export const MAX_PASSKEY_NAME = 64;

export const passkeyNameSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, "passkeys.form.nameRequired" satisfies MessageKey)
    .max(MAX_PASSKEY_NAME, "passkeys.form.nameTooLong" satisfies MessageKey),
});

export type PasskeyNameForm = z.infer<typeof passkeyNameSchema>;

/** Whether this browser can create and use passkeys with JSON options (Chrome 129+, Firefox 119+, Safari 18.4+). */
export function passkeysSupported(): boolean {
  return (
    typeof window !== "undefined" &&
    typeof window.PublicKeyCredential !== "undefined" &&
    typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function" &&
    typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function"
  );
}

/** Whether the browser can offer passkeys in the user name field's autofill. */
export async function autofillSupported(): Promise<boolean> {
  return (
    passkeysSupported() &&
    typeof PublicKeyCredential.isConditionalMediationAvailable === "function" &&
    (await PublicKeyCredential.isConditionalMediationAvailable())
  );
}

/** Asks the browser for a new passkey; returns the credential as JSON for the aggregator. */
export async function createPasskey(options: unknown): Promise<string> {
  const publicKey = PublicKeyCredential.parseCreationOptionsFromJSON(options as PublicKeyCredentialCreationOptionsJSON);
  const credential = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null;
  if (credential === null) {
    throw new DOMException("No passkey was created.", "NotAllowedError");
  }

  return JSON.stringify(credential.toJSON());
}

/**
 * Asks the browser to sign in with a passkey; returns the assertion as JSON for the aggregator. With
 * `mediation: "conditional"` the browser waits quietly and offers passkeys in the user name field's autofill.
 */
export async function getPasskey(options: unknown, mediation?: CredentialMediationRequirement, signal?: AbortSignal): Promise<string> {
  const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(options as PublicKeyCredentialRequestOptionsJSON);
  const credential = (await navigator.credentials.get({ publicKey, mediation, signal })) as PublicKeyCredential | null;
  if (credential === null) {
    throw new DOMException("No passkey was chosen.", "NotAllowedError");
  }

  return JSON.stringify(credential.toJSON());
}

/** The person closed the browser's dialog or let it time out: nothing to report. */
export function isCancelled(error: unknown): boolean {
  return error instanceof DOMException && (error.name === "NotAllowedError" || error.name === "AbortError");
}

/** The message for a failed browser ceremony; null when the person cancelled it. */
export function browserErrorKey(error: unknown): MessageKey | null {
  if (isCancelled(error)) {
    return null;
  }

  if (error instanceof DOMException && error.name === "InvalidStateError") {
    return "passkeys.errors.alreadyRegistered";
  }

  if (error instanceof DOMException && error.name === "SecurityError") {
    return "passkeys.errors.insecure";
  }

  return null;
}
