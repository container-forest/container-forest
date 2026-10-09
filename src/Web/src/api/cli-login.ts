import { useMutation } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import type { components } from "./schema.gen";

/** Approves a CLI login: the CLI gets a token of the signed-in user with these limits when it next polls. */
export function useApproveCliAuthorizationMutation(userCode: string) {
  return useMutation({
    mutationFn: async (request: components["schemas"]["ApproveCliAuthorizationRequest"]) =>
      unwrap(await api.POST("/api/cli/authorizations/{userCode}/approve", { params: { path: { userCode } }, body: request })),
  });
}

/** Denies a CLI login: the CLI is told so and gets nothing. */
export function useDenyCliAuthorizationMutation(userCode: string) {
  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/cli/authorizations/{userCode}/deny", { params: { path: { userCode } } })),
  });
}
