import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@/api/client";
import type { MessageKey } from "@/i18n";

/** A field's validation message; zod schemas give translation keys, translated here so a language switch applies. */
export function FieldError({ id, message }: { id: string; message: string | undefined }) {
  const { t } = useTranslation();

  return message ? (
    <p id={id} className="text-destructive text-sm">
      {t(message as MessageKey)}
    </p>
  ) : null;
}

/** An error about the whole form or action, announced to screen readers. */
export function ErrorText({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="text-destructive text-sm">
      {children}
    </p>
  );
}

/**
 * Why the aggregator rejected a password, under a translated heading. The reasons are the aggregator's English
 * texts: the policy lives there, and listing them is better than a vague "invalid password".
 */
export function PasswordReasons({ error, heading }: { error: unknown; heading: string }) {
  const reasons = error instanceof ApiError ? (error.problem?.errors ?? []) : [];
  if (reasons.length === 0) {
    return null;
  }

  return (
    <div role="alert" className="text-destructive text-sm">
      <p>{heading}</p>
      <ul className="mt-1 list-disc pl-5" lang="en">
        {reasons.map((reason) => (
          <li key={reason}>{reason}</li>
        ))}
      </ul>
    </div>
  );
}
