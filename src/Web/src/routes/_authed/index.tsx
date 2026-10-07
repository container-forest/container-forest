import { createFileRoute, redirect } from "@tanstack/react-router";
import { readRememberedEnvironment } from "@/features/navigation/model";

/**
 * The start page is the stacks of the environment the user worked with last; the stacks screen falls back to the
 * first environment when that one is gone or was never chosen.
 */
export const Route = createFileRoute("/_authed/")({
  beforeLoad: () => {
    throw redirect({ to: "/stacks", search: { environment: readRememberedEnvironment() }, replace: true });
  },
});
