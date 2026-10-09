import { z } from "zod";
import type { components } from "@/api/schema.gen";
import type { MessageKey } from "@/i18n";
import { MAX_TOKEN_NAME } from "@/features/tokens/model";

// Signing the CLI in through the browser (ADR-0008) without React: the page's address, the user code and where
// the browser goes after the decision.

export type CliAuthorization = components["schemas"]["CliAuthorizationResponse"];

/** CliAuthorization.UserCodeLength in the aggregator; shown as two groups of four. */
export const USER_CODE_LENGTH = 8;

/** OneTimeCode.Alphabet in the aggregator: no look-alikes. */
const USER_CODE_ALPHABET = /^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]+$/;

/** The code as the person may type it: case, spaces and dashes do not matter. */
export function normalizeUserCode(value: string): string {
  return value.replace(/[\s-]/g, "").toUpperCase();
}

export function formatUserCode(value: string): string {
  const code = normalizeUserCode(value);
  return code.length === USER_CODE_LENGTH ? `${code.slice(0, 4)}-${code.slice(4)}` : code;
}

export const userCodeSchema = z.object({
  code: z
    .string()
    .transform(normalizeUserCode)
    .pipe(
      z
        .string()
        .length(USER_CODE_LENGTH, "cliLogin.device.codeInvalid" satisfies MessageKey)
        .regex(USER_CODE_ALPHABET, "cliLogin.device.codeInvalid" satisfies MessageKey),
    ),
});

export type UserCodeForm = z.input<typeof userCodeSchema>;

/**
 * Search params of the approval page. `port` is where the CLI listens on 127.0.0.1 to hear the decision at once;
 * without it the CLI finds out on its next poll.
 */
export const cliAuthorizeSearchSchema = z.object({
  code: z.string().max(32).catch(""),
  port: z.coerce.number().int().min(1024).max(65535).optional().catch(undefined),
});

/**
 * Where the browser goes after the decision when the CLI listens locally. Always the loopback address and a port
 * from the CLI's own link: the page never sends the browser anywhere else, and nothing secret goes with it.
 */
export function loopbackUrl(port: number, decision: "approved" | "denied"): string {
  return `http://127.0.0.1:${port}/done?result=${decision}`;
}

/** A token name that says which machine it is for, e.g. "forest — laptop". */
export function defaultTokenName(hostName: string): string {
  return `forest — ${hostName}`.slice(0, MAX_TOKEN_NAME);
}
