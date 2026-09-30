import { Link, Outlet, createFileRoute, redirect } from "@tanstack/react-router";
import { isUnauthorized } from "../api/client";
import { meQuery } from "../api/queries";

/**
 * Every page that needs a signed-in user lives under this pathless layout. A 401 from /api/me sends the
 * user to /login, which brings them back here afterwards.
 */
export const Route = createFileRoute("/_authed")({
  beforeLoad: async ({ context, location }) => {
    try {
      return { me: await context.queryClient.ensureQueryData(meQuery) };
    } catch (error) {
      if (isUnauthorized(error)) {
        throw redirect({ to: "/login", search: { redirect: location.href } });
      }

      throw error;
    }
  },
  component: AuthedLayout,
});

function AuthedLayout() {
  const { me } = Route.useRouteContext();

  return (
    <div className="layout">
      <header className="topbar">
        <Link to="/" className="brand">
          container-forest
        </Link>
        <nav className="nav">{/* Screens add their links here. */}</nav>
        <span className="user">
          {me.userName}
          {me.isAdmin && " (адміністратор)"}
        </span>
      </header>
      <main className="page">
        <Outlet />
      </main>
    </div>
  );
}
