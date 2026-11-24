import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { backupSettingsQuery, backupStatusQuery } from "./queries";
import type { components } from "./schema.gen";

export type UpdateBackupSettingsRequest = components["schemas"]["UpdateBackupSettingsRequest"];

/** Saves the settings; the request carries a fresh identity confirmation. */
export function useUpdateBackupSettingsMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (body: UpdateBackupSettingsRequest) => unwrap(await api.PUT("/api/settings/backup", { body })),
    onSuccess: (settings) => {
      queryClient.setQueryData(backupSettingsQuery.queryKey, settings);
      void queryClient.invalidateQueries({ queryKey: backupStatusQuery.queryKey });
    },
  });
}

/** Makes a backup and waits for it to end; a backup that failed is an ordinary answer with its error. */
export function useRunBackupMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/backup/run")),
    onSettled: () => queryClient.invalidateQueries({ queryKey: backupStatusQuery.queryKey }),
  });
}

/**
 * A fresh proof of identity with the password, and with two-factor on (ADR-0019) the current code of the app;
 * returns the token for the action that needs it.
 */
export function useConfirmWithPasswordMutation() {
  return useMutation({
    mutationFn: async ({ password, code }: { password: string; code?: string }) =>
      unwrap(await api.POST("/api/auth/confirm/password", { body: { password, code: code || null } })).token,
  });
}

/** Asks the aggregator for options and hands them to `ask`, the browser's dialog; returns the token. */
export function useConfirmWithPasskeyMutation() {
  return useMutation({
    mutationFn: async (ask: (options: unknown) => Promise<string>) => {
      const options: unknown = unwrap(await api.POST("/api/auth/confirm/passkey/options"));
      const credential = await ask(options);
      return unwrap(await api.POST("/api/auth/confirm/passkey", { body: { credential } })).token;
    },
  });
}
