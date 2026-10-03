import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { gitConnectionsQuery, repositoriesQuery } from "./queries";
import type { components } from "./schema.gen";

export type CreateGitConnectionRequest = components["schemas"]["CreateGitConnectionRequest"];
export type UpdateGitConnectionRequest = components["schemas"]["UpdateGitConnectionRequest"];
export type GitAccessTokenRequest = components["schemas"]["GitAccessTokenRequest"];

// A connection's access token is a secret: mutations that carry it are dropped as soon as nothing observes them
// (gcTime 0), and their forms reset the mutation after success, as for registry passwords. Repositories show their
// connection's name, so they are reloaded with the connections.

function useInvalidateConnections() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: gitConnectionsQuery.queryKey }),
      queryClient.invalidateQueries({ queryKey: repositoriesQuery.queryKey }),
    ]);
}

export function useCreateGitConnectionMutation() {
  const invalidate = useInvalidateConnections();

  return useMutation({
    gcTime: 0,
    mutationFn: async (request: CreateGitConnectionRequest) => unwrap(await api.POST("/api/git-connections", { body: request })),
    onSuccess: invalidate,
  });
}

export function useUpdateGitConnectionMutation(connectionId: string) {
  const invalidate = useInvalidateConnections();

  return useMutation({
    mutationFn: async (request: UpdateGitConnectionRequest) =>
      unwrap(await api.PUT("/api/git-connections/{id}", { params: { path: { id: connectionId } }, body: request })),
    onSuccess: invalidate,
  });
}

export function useDeleteGitConnectionMutation(connectionId: string) {
  const invalidate = useInvalidateConnections();

  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/git-connections/{id}", { params: { path: { id: connectionId } } })),
    onSuccess: invalidate,
  });
}

export function useSetGitConnectionTokenMutation(connectionId: string) {
  const invalidate = useInvalidateConnections();

  return useMutation({
    gcTime: 0,
    mutationFn: async (request: GitAccessTokenRequest) =>
      unwrap(await api.PUT("/api/git-connections/{id}/access-token", { params: { path: { id: connectionId } }, body: request })),
    onSuccess: invalidate,
  });
}

export function useRemoveGitConnectionTokenMutation(connectionId: string) {
  const invalidate = useInvalidateConnections();

  return useMutation({
    mutationFn: async () =>
      unwrap(await api.DELETE("/api/git-connections/{id}/access-token", { params: { path: { id: connectionId } } })),
    onSuccess: invalidate,
  });
}

/** A new organization-wide webhook secret; shown once, as for a repository's (see useCreateWebhookSecretMutation). */
export function useCreateConnectionWebhookMutation(connectionId: string) {
  const invalidate = useInvalidateConnections();

  return useMutation({
    gcTime: 0,
    mutationFn: async () =>
      unwrap(await api.POST("/api/git-connections/{id}/webhook", { params: { path: { id: connectionId } } })),
    onSuccess: invalidate,
  });
}

export function useRemoveConnectionWebhookMutation(connectionId: string) {
  const invalidate = useInvalidateConnections();

  return useMutation({
    mutationFn: async () =>
      unwrap(await api.DELETE("/api/git-connections/{id}/webhook", { params: { path: { id: connectionId } } })),
    onSuccess: invalidate,
  });
}
