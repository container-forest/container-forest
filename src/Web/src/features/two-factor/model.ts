import { encode } from "uqr";

/** The code of an authenticator app without the space some apps show in the middle. */
export function normalizeCode(code: string): string {
  return code.replace(/\s+/g, "");
}

/** Six digits, as the aggregator accepts them (TOTP, ADR-0019). */
export function isTwoFactorCode(code: string): boolean {
  return /^\d{6}$/.test(normalizeCode(code));
}

/** The key in groups of four, easier to type into an app by hand. */
export function groupKey(key: string): string {
  return key.replace(/(.{4})(?=.)/g, "$1 ");
}

/**
 * The QR code of an otpauth:// link as rows of dark modules, drawn by the page itself: the key never leaves the
 * browser, and no generated markup is injected.
 */
export function qrModules(text: string): boolean[][] {
  return encode(text, { ecc: "M", border: 2 }).data;
}

/** The recovery codes as a text file to keep. */
export function recoveryCodesFile(userName: string, codes: readonly string[]): string {
  return [`container-forest recovery codes for ${userName}`, "Each code signs in once instead of the app's code.", "", ...codes, ""].join("\n");
}
