import { Link, Outlet, createFileRoute, redirect, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { useLogoutMutation } from "../api/auth";
import { isUnauthorized } from "../api/client";
import { meQuery } from "../api/queries";
import { LogOutIcon } from "../components/icons";
import { LanguageSwitcher } from "../components/language-switcher";
import { Button } from "../components/ui/button";

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
  const { t } = useTranslation();
  const { me } = Route.useRouteContext();

  return (
    <div className="min-h-svh">
      <header className="flex items-center gap-6 border-b px-6 py-3">
        <Link to="/" className="font-semibold">
          container-forest
        </Link>
        <nav className="flex flex-1 gap-4 text-sm">
          {/* Screens add their links here. */}
          <Link
            to="/containers"
            className="hover:text-foreground"
            inactiveProps={{ className: "text-muted-foreground" }}
            activeProps={{ className: "text-foreground font-medium" }}
          >
            {t("layout.nav.containers")}
          </Link>
          <Link
            to="/stacks"
            className="hover:text-foreground"
            inactiveProps={{ className: "text-muted-foreground" }}
            activeProps={{ className: "text-foreground font-medium" }}
          >
            {t("layout.nav.stacks")}
          </Link>
          <Link
            to="/discord"
            className="hover:text-foreground"
            inactiveProps={{ className: "text-muted-foreground" }}
            activeProps={{ className: "text-foreground font-medium" }}
          >
            {t("layout.nav.discord")}
          </Link>
          {/* Only a convenience: the aggregator refuses non-administrators anyway. */}
          {me.isAdmin && (
            <Link
              to="/repositories"
              className="hover:text-foreground"
              inactiveProps={{ className: "text-muted-foreground" }}
              activeProps={{ className: "text-foreground font-medium" }}
            >
              {t("layout.nav.repositories")}
            </Link>
          )}
          {me.isAdmin && (
            <Link
              to="/users"
              className="hover:text-foreground"
              inactiveProps={{ className: "text-muted-foreground" }}
              activeProps={{ className: "text-foreground font-medium" }}
            >
              {t("layout.nav.users")}
            </Link>
          )}
        </nav>
        <Link to="/account" className="text-muted-foreground hover:text-foreground text-sm" title={t("layout.nav.account")}>
          {me.userName}
          {me.isAdmin && ` ${t("layout.administrator")}`}
        </Link>
        <LanguageSwitcher />
        <LogoutButton />
      </header>
      <main className="mx-auto max-w-6xl p-6">
        <Outlet />
      </main>
    </div>
  );
}

function LogoutButton() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const logout = useLogoutMutation();

  return (
    <Button
      variant="ghost"
      size="sm"
      disabled={logout.isPending}
      onClick={() => logout.mutate(undefined, { onSuccess: () => navigate({ to: "/login" }) })}
    >
      <LogOutIcon data-icon="inline-start" />
      {t("layout.signOut")}
    </Button>
  );
}
