import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";

// Git connections (ADR-0006) without React: forms and the address rules the aggregator applies.

export type GitConnection = components["schemas"]["GitConnectionResponse"];

/** GitConnection.MaxNameLength in the aggregator. */
export const MAX_CONNECTION_NAME = 64;

/** GitConnection.MaxAccessUserNameLength and GitAccessToken.MaxTokenLength in the aggregator. */
export const MAX_ACCESS_USER_NAME = 128;
export const MAX_ACCESS_TOKEN = 1024;

const accessUserName = z
  .string()
  .trim()
  .max(MAX_ACCESS_USER_NAME, "gitConnections.token.userNameInvalid" satisfies MessageKey)
  // ':' would split the user name from the token in HTTP basic authentication.
  .regex(/^[^:\p{Cc}]*$/u, "gitConnections.token.userNameInvalid" satisfies MessageKey);

// Not trimmed: a space inside a pasted token is a mistake the aggregator rejects, so it is shown here too.
const accessToken = z
  .string()
  .min(1, "gitConnections.token.tokenRequired" satisfies MessageKey)
  .max(MAX_ACCESS_TOKEN, "gitConnections.token.tokenInvalid" satisfies MessageKey)
  .regex(/^\S+$/, "gitConnections.token.tokenInvalid" satisfies MessageKey);

export const accessTokenSchema = z.object({ userName: accessUserName, token: accessToken });

export type AccessTokenForm = z.infer<typeof accessTokenSchema>;

/** A blank user name is sent as null: the aggregator then uses its default. */
export function toAccessTokenRequest(form: AccessTokenForm) {
  return { userName: form.userName.trim() === "" ? null : form.userName.trim(), token: form.token };
}

// Like the aggregator's GitUrl.Parse: https, no credentials, query, fragment or percent-encoding in the path.
const httpsUrl = z
  .string()
  .trim()
  .refine((value) => parseGitUrl(value) !== null, "gitConnections.form.prefixInvalid" satisfies MessageKey);

const connectionDetails = {
  name: z
    .string()
    .trim()
    .min(1, "gitConnections.form.nameInvalid" satisfies MessageKey)
    .max(MAX_CONNECTION_NAME, "gitConnections.form.nameInvalid" satisfies MessageKey),
  urlPrefix: httpsUrl,
  allEnvironments: z.boolean(),
  environmentIds: z.array(z.string()),
};

export const connectionDetailsSchema = z.object(connectionDetails);

export type ConnectionDetailsForm = z.infer<typeof connectionDetailsSchema>;

/** "New connection": the token fields are checked only when a token is given. */
export const createConnectionSchema = z
  .object({ ...connectionDetails, withToken: z.boolean(), userName: z.string(), token: z.string() })
  .superRefine((form, context) => {
    if (!form.withToken) {
      return;
    }

    for (const [field, schema] of [["userName", accessUserName], ["token", accessToken]] as const) {
      const result = schema.safeParse(form[field]);
      if (!result.success) {
        context.addIssue({ code: "custom", path: [field], message: result.error.issues[0]?.message });
      }
    }
  });

export type CreateConnectionForm = z.infer<typeof createConnectionSchema>;

export function toConnectionDetailsRequest(form: ConnectionDetailsForm) {
  return {
    name: form.name,
    urlPrefix: form.urlPrefix,
    allEnvironments: form.allEnvironments,
    environmentIds: form.allEnvironments ? [] : form.environmentIds,
  };
}

/** The token fields are ignored without "with token", even if filled in. */
export function toCreateConnectionRequest(form: CreateConnectionForm) {
  return {
    ...toConnectionDetailsRequest(form),
    accessToken: form.withToken ? toAccessTokenRequest({ userName: form.userName, token: form.token }) : null,
  };
}

/** The form's values for an existing connection. */
export function connectionDetailsOf(connection: GitConnection): ConnectionDetailsForm {
  return {
    name: connection.name,
    urlPrefix: connection.urlPrefix,
    allEnvironments: connection.allEnvironments,
    environmentIds: [...connection.environmentIds],
  };
}

/** Adds or removes one environment, keeping the list without duplicates. */
export function toggleEnvironment(ids: readonly string[], id: string, checked: boolean): string[] {
  return checked ? [...new Set([...ids, id])] : ids.filter((other) => other !== id);
}

/** An https URL the aggregator accepts for a repository or a prefix; null otherwise. */
export function parseGitUrl(value: string): URL | null {
  let url: URL;
  try {
    url = new URL(value.trim());
  } catch {
    return null;
  }

  const raw = value.trim();
  const path = raw.slice(raw.indexOf("//") + 2).replace(/^[^/]*/, "");
  if (url.protocol !== "https:" || url.username !== "" || url.password !== "" || url.search !== "" || url.hash !== "") {
    return null;
  }

  return path.includes("%") || path.includes("\\") ? null : url;
}

// The path's segments as the aggregator compares them: case-insensitive, ".git" at the end of a segment ignored.
function segments(url: URL): string[] {
  return url.pathname
    .split("/")
    .filter((segment) => segment !== "")
    .map((segment) => segment.toLowerCase().replace(/\.git$/, ""));
}

/**
 * Whether a repository address lies under a connection's prefix: the same host and port and all of the prefix's
 * path segments ("team/" covers "team/app.git" but not "team-b/app"). GitUrl.Covers in the aggregator has the last word.
 */
export function coversUrl(prefix: string, url: string): boolean {
  const base = parseGitUrl(prefix);
  const target = parseGitUrl(url);
  if (base === null || target === null || base.host.toLowerCase() !== target.host.toLowerCase()) {
    return false;
  }

  const prefixSegments = segments(base);
  const urlSegments = segments(target);
  return prefixSegments.length <= urlSegments.length && prefixSegments.every((segment, i) => segment === urlSegments[i]);
}

/**
 * Where the git server sends an organization's pushes. The panel's own origin: Caddy is the only entrance and
 * proxies /api to the aggregator (ADR-0005).
 */
export function connectionWebhookUrl(origin: string, connectionId: string): string {
  return `${origin.replace(/\/+$/, "")}/api/webhooks/connections/${connectionId}`;
}
