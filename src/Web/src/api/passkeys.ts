import { useMutation, useQueryClient } from "@tanstack/react-query";
import { createPasskey, getPasskey } from "@/features/passkeys/model";
import { api, unwrap } from "./client";
import { meQuery, passkeysQuery, userPasskeysQuery } from "./queries";

/**
 * Signs in with a passkey: options from the aggregator, the browser's dialog, the aggregator checks the answer and
 * sets the session cookie. The two requests must come from the same browser: the challenge is in a cookie.
 */
export async function signInWithPasskey({
  rememberMe,
  mediation,
  signal,
}: {
  /** Read when the person has chosen a passkey: with autofill that is long after the ceremony started. */
  rememberMe: () => boolean;
  mediation?: CredentialMediationRequirement;
  signal?: AbortSignal;
}) {
  const options = unwrap(await api.POST("/api/auth/passkey/options", { signal }));
  const credential = await getPasskey(options, mediation, signal);
  return unwrap(await api.POST("/api/auth/passkey/login", { body: { credential, rememberMe: rememberMe() }, signal }));
}

/** Like the password login: what was cached belonged to whoever was signed in before. */
export function usePasskeyLoginMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (rememberMe: boolean) => signInWithPasskey({ rememberMe: () => rememberMe }),
    onSuccess: (me) => {
      queryClient.removeQueries();
      queryClient.setQueryData(meQuery.queryKey, me);
    },
  });
}

/** Creates a passkey in the browser and adds it to the signed-in user under `name`. */
export function useAddPasskeyMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (name: string) => {
      const options = unwrap(await api.POST("/api/me/passkeys/options"));
      const credential = await createPasskey(options);
      return unwrap(await api.POST("/api/me/passkeys", { body: { credential, name } }));
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: passkeysQuery.queryKey }),
  });
}

export function useRenamePasskeyMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ id, name }: { id: string; name: string }) =>
      unwrap(await api.PUT("/api/me/passkeys/{id}", { params: { path: { id } }, body: { name } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: passkeysQuery.queryKey }),
  });
}

export function useRemovePasskeyMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/me/passkeys/{id}", { params: { path: { id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: passkeysQuery.queryKey }),
  });
}

/** An administrator removes another user's passkey, e.g. of a lost device. */
export function useRemoveUserPasskeyMutation(userId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.DELETE("/api/users/{userId}/passkeys/{id}", { params: { path: { userId, id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: userPasskeysQuery(userId).queryKey }),
  });
}
