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
      {/* Narrower than xl, the links get a row of their own and scroll sideways instead of widening the page. */}
      <header className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b sm:gap-x-6 px-4 py-3 sm:px-6">
        <Link to="/" className="font-semibold">
          container-forest
        </Link>
        <nav className="order-last -mx-4 flex w-full gap-4 overflow-x-auto px-4 pb-1 text-sm whitespace-nowrap sm:-mx-6 sm:px-6 xl:order-none xl:mx-0 xl:w-auto xl:flex-1 xl:overflow-visible xl:px-0 xl:pb-0">
          {/* Screens add their links here. */}
          <Link
            to="/environments"
            className="hover:text-foreground"
            inactiveProps={{ className: "text-muted-foreground" }}
            activeProps={{ className: "text-foreground font-medium" }}
          >
            {t("layout.nav.environments")}
          </Link>
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
            to="/keys"
            className="hover:text-foreground"
            inactiveProps={{ className: "text-muted-foreground" }}
            activeProps={{ className: "text-foreground font-medium" }}
          >
            {t("layout.nav.keys")}
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
          {me.isAdmin && (
            <Link
              to="/notifications"
              className="hover:text-foreground"
              inactiveProps={{ className: "text-muted-foreground" }}
              activeProps={{ className: "text-foreground font-medium" }}
            >
              {t("layout.nav.notifications")}
            </Link>
          )}
        </nav>
        <Link
          to="/account"
          className="text-muted-foreground hover:text-foreground ml-auto max-w-32 truncate text-sm sm:max-w-none"
          title={t("layout.nav.account")}
        >
          {me.userName}
          {me.isAdmin && <span className="hidden sm:inline"> {t("layout.administrator")}</span>}
        </Link>
        <LanguageSwitcher />
        <LogoutButton />
      </header>
      <main className="mx-auto max-w-6xl p-4 sm:p-6">
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
      {/* On a phone only the icon fits next to the name; the text stays for screen readers. */}
      <span className="sr-only sm:not-sr-only">{t("layout.signOut")}</span>
    </Button>
  );
}
