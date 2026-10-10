import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// Personal API tokens without React: the form, the request it becomes and how a token is shown.

export type ApiToken = components["schemas"]["ApiTokenResponse"];
export type CreateApiTokenRequest = components["schemas"]["CreateApiTokenRequest"];

/** ApiToken.MaxNameLength in the aggregator. */
export const MAX_TOKEN_NAME = 64;

/** Lifetimes offered when creating a token; "never" has to be chosen on purpose. */
export const TOKEN_LIFETIMES = ["30", "90", "365", "never"] as const;
export type TokenLifetime = (typeof TOKEN_LIFETIMES)[number];

export const DEFAULT_TOKEN_LIFETIME: TokenLifetime = "90";

export const createTokenSchema = z
  .object({
    name: z
      .string()
      .trim()
      .min(1, "tokens.form.nameRequired" satisfies MessageKey)
      .max(MAX_TOKEN_NAME, "tokens.form.nameTooLong" satisfies MessageKey),
    lifetime: z.enum(TOKEN_LIFETIMES),
    readOnly: z.boolean(),
    secrets: z.boolean(),
    allEnvironments: z.boolean(),
    environmentIds: z.array(z.string()),
  })
  .refine((form) => form.allEnvironments || form.environmentIds.length > 0, {
    path: ["environmentIds"],
    message: "tokens.form.environmentsRequired" satisfies MessageKey,
  })
  .refine((form) => !(form.readOnly && form.secrets), {
    path: ["secrets"],
    message: "tokens.form.secretsReadOnly" satisfies MessageKey,
  });

export type CreateTokenForm = z.infer<typeof createTokenSchema>;

export const emptyCreateTokenForm: CreateTokenForm = {
  name: "",
  lifetime: DEFAULT_TOKEN_LIFETIME,
  readOnly: false,
  secrets: false,
  allEnvironments: true,
  environmentIds: [],
};

/** The expiry of a token created `now` with `lifetime`; null when it does not expire. */
export function expiryOf(lifetime: TokenLifetime, now: Date): string | null {
  if (lifetime === "never") {
    return null;
  }

  const expiresAt = new Date(now);
  expiresAt.setDate(expiresAt.getDate() + Number(lifetime));
  return expiresAt.toISOString();
}

export function toCreateTokenRequest(form: CreateTokenForm, now: Date): CreateApiTokenRequest {
  return {
    name: form.name.trim(),
    readOnly: form.readOnly,
    environmentIds: form.allEnvironments ? null : form.environmentIds,
    expiresAt: expiryOf(form.lifetime, now),
    secrets: form.secrets,
  };
}

export type TokenState = "active" | "expired" | "expiresSoon";

/** How long before its expiry a token is marked as expiring soon. */
const EXPIRES_SOON_MS = 7 * 24 * 60 * 60 * 1000;

export function tokenState(token: ApiToken, now: Date): TokenState {
  if (token.expired) {
    return "expired";
  }

  return token.expiresAt !== null && new Date(token.expiresAt).getTime() - now.getTime() < EXPIRES_SOON_MS
    ? "expiresSoon"
    : "active";
}

/** Names of the environments a limited token reaches, in the order of `environments`; unknown ids are left out. */
export function environmentNames(token: ApiToken, environments: readonly { id: string; name: string }[]): string[] | null {
  if (token.environmentIds === null) {
    return null;
  }

  const ids = new Set(token.environmentIds);
  return environments.filter((environment) => ids.has(environment.id)).map((environment) => environment.name);
}
