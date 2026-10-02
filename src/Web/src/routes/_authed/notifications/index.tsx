import { createFileRoute, redirect } from "@tanstack/react-router";

/** The header links to /notifications; channels are where the work starts. */
export const Route = createFileRoute("/_authed/notifications/")({
  beforeLoad: () => {
    throw redirect({ to: "/notifications/channels", replace: true });
  },
});
