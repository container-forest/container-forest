import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { meQuery, signInSettingsQuery, twoFactorQuery, usersQuery } from "./queries";

// Two-factor authentication with an authenticator app (ADR-0019). Turning it on or off changes the account's
// security stamp: the aggregator ends the user's other sessions and renews this one's cookie.

/** A new key for the app; the request carries a fresh identity confirmation. Nothing is on until a code turns it on. */
export function useBeginTwoFactorSetupMutation() {
  return useMutation({
    gcTime: 0,
    mutationFn: async (confirmation: string) => unwrap(await api.POST("/api/me/two-factor/setup", { body: { confirmation } })),
  });
}

/** Turns two-factor on with the current code of the app; the answer holds the recovery codes, shown once. */
export function useEnableTwoFactorMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async (code: string) => unwrap(await api.POST("/api/me/two-factor/enable", { body: { code } })),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: twoFactorQuery.queryKey }),
        queryClient.invalidateQueries({ queryKey: meQuery.queryKey, exact: true }),
      ]),
  });
}

/** New recovery codes, shown once; the old ones stop working. */
export function useRegenerateRecoveryCodesMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async (confirmation: string) =>
      unwrap(await api.POST("/api/me/two-factor/recovery-codes", { body: { confirmation } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: twoFactorQuery.queryKey }),
  });
}

export function useDisableTwoFactorMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (confirmation: string) => unwrap(await api.POST("/api/me/two-factor/disable", { body: { confirmation } })),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: twoFactorQuery.queryKey }),
        queryClient.invalidateQueries({ queryKey: meQuery.queryKey, exact: true }),
      ]),
  });
}

/** For administrators: turns off another user's two-factor, e.g. after a lost phone. */
export function useResetUserTwoFactorMutation(userId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/users/{id}/two-factor", { params: { path: { id: userId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: usersQuery.queryKey }),
  });
}

/** Saves whether administrators must have two-factor; the request carries a fresh identity confirmation. */
export function useUpdateSignInSettingsMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (body: { requireTwoFactorForAdministrators: boolean; requireTwoFactorAfterDiscord: boolean; confirmation: string }) =>
      unwrap(await api.PUT("/api/settings/sign-in", { body })),
    onSuccess: (settings) => {
      queryClient.setQueryData(signInSettingsQuery.queryKey, settings);
      // The administrator's own session may now be limited to setting up two-factor.
      return queryClient.invalidateQueries({ queryKey: meQuery.queryKey, exact: true });
    },
  });
}
