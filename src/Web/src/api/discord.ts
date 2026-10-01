import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { discordLinkQuery } from "./queries";
import type { components } from "./schema.gen";

export type DiscordLinkCode = components["schemas"]["DiscordLinkCodeResponse"];

/**
 * Issues a one-time code for /link in Discord. A new code replaces the previous one, and the link itself
 * changes only once the bot redeems it, so nothing cached is stale yet.
 */
export function useIssueDiscordLinkCodeMutation() {
  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/me/discord/link-code")),
  });
}

/** Removes the link: the bot no longer acts for this user. */
export function useUnlinkDiscordMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/me/discord")),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: discordLinkQuery.queryKey }),
  });
}
