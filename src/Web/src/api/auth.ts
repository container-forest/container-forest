import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { meQuery } from "./queries";
import type { components } from "./schema.gen";

export type LoginRequest = components["schemas"]["LoginRequest"];

export type Me = components["schemas"]["MeResponse"];

/** A password either signs in, or (with two-factor on, ADR-0019) asks for the code next. */
export type LoginResult = { kind: "signedIn"; me: Me } | { kind: "twoFactor" };

export type ChangePasswordRequest = components["schemas"]["ChangePasswordRequest"];

/**
 * Signs in with a password; the aggregator sets the cf_session cookie. Whatever was cached belonged to whoever
 * was signed in before, so it is dropped and the new user is cached as `me`. A 202 means the password was right and
 * the account wants the code of its authenticator app: the aggregator keeps that step in the cf_2fa cookie.
 */
export function useLoginMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: LoginRequest): Promise<LoginResult> => {
      const result = await api.POST("/api/auth/login", { body: request });
      const body = unwrap(result);
      return result.response.status === 202 ? { kind: "twoFactor" } : { kind: "signedIn", me: body as Me };
    },
    onSuccess: (result) => {
      if (result.kind === "signedIn") {
        queryClient.removeQueries();
        queryClient.setQueryData(meQuery.queryKey, result.me);
      }
    },
  });
}

/** The second step: a code of the app, or a recovery code. */
export function useTwoFactorLoginMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (code: string) => unwrap(await api.POST("/api/auth/login/two-factor", { body: { code } })),
    onSuccess: (me) => {
      queryClient.removeQueries();
      queryClient.setQueryData(meQuery.queryKey, me);
    },
  });
}

/** Ends the session and drops everything cached for the signed-in user. */
export function useLogoutMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/auth/logout")),
    onSuccess: () => queryClient.removeQueries(),
  });
}

/**
 * Changes the signed-in user's own password. The aggregator ends their other sessions and renews this one's
 * cookie, so nothing cached changes.
 */
export function useChangePasswordMutation() {
  return useMutation({
    mutationFn: async (request: ChangePasswordRequest) => unwrap(await api.POST("/api/auth/password", { body: request })),
  });
}
