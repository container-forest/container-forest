import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { ChannelForm, CreateNotificationRuleRequest } from "@/features/notifications/model";
import { api, unwrap } from "./client";
import { notificationChannelsQuery, notificationRulesQuery } from "./queries";

export interface UpdateChannelVariables extends ChannelForm {
  isEnabled: boolean;
}

export interface UpdateRuleVariables {
  /** Kind names; a kind this panel does not know is passed through unchanged. */
  events: string[];
  isEnabled: boolean;
}

/** Every change to a channel shows in the list, so the list is refetched. */
function useInvalidateChannels() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: notificationChannelsQuery.queryKey, exact: true });
}

export function useCreateChannelMutation() {
  const invalidate = useInvalidateChannels();

  return useMutation({
    mutationFn: async (request: ChannelForm) => unwrap(await api.POST("/api/notification-channels", { body: request })),
    onSuccess: invalidate,
  });
}

export function useUpdateChannelMutation(id: string) {
  const invalidate = useInvalidateChannels();

  return useMutation({
    mutationFn: async (request: UpdateChannelVariables) =>
      unwrap(await api.PUT("/api/notification-channels/{id}", { params: { path: { id } }, body: request })),
    onSuccess: invalidate,
  });
}

/** The channel's rules go with it; its history stays, so the history is refetched to show the channel as deleted. */
export function useDeleteChannelMutation(id: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/notification-channels/{id}", { params: { path: { id } } })),
    onSuccess: () => {
      queryClient.removeQueries({ queryKey: notificationRulesQuery(id).queryKey });
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: notificationChannelsQuery.queryKey, exact: true }),
        queryClient.invalidateQueries({ queryKey: ["notifications"] }),
      ]);
    },
  });
}

/** A rule changes only its channel's list. */
function useInvalidateRules(channelId: string) {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: notificationRulesQuery(channelId).queryKey });
}

export function useCreateRuleMutation(channelId: string) {
  const invalidate = useInvalidateRules(channelId);

  return useMutation({
    mutationFn: async (request: CreateNotificationRuleRequest) =>
      unwrap(
        await api.POST("/api/notification-channels/{id}/rules", { params: { path: { id: channelId } }, body: request }),
      ),
    onSuccess: invalidate,
  });
}

export function useUpdateRuleMutation(channelId: string, ruleId: string) {
  const invalidate = useInvalidateRules(channelId);

  return useMutation({
    mutationFn: async (request: UpdateRuleVariables) =>
      unwrap(await api.PUT("/api/notification-rules/{id}", { params: { path: { id: ruleId } }, body: request })),
    onSuccess: invalidate,
  });
}

export function useDeleteRuleMutation(channelId: string, ruleId: string) {
  const invalidate = useInvalidateRules(channelId);

  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/notification-rules/{id}", { params: { path: { id: ruleId } } })),
    onSuccess: invalidate,
  });
}
