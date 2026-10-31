import { Link, Outlet, createFileRoute, redirect } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { isUnauthorized } from "@/api/client";
import { meQuery } from "@/api/queries";
import { MenuIcon } from "@/components/icons";
import { SidebarContent } from "@/components/layout/app-sidebar";
import { HubProvider } from "@/components/live/hub-provider";
import { LiveIndicator } from "@/components/live/live-indicator";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { Brand } from "@/components/layout/brand";
import { ConnectionError } from "@/components/layout/connection-error";

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
  // Without /api/me nothing below can show; a lost connection gets its own screen, not a blank one.
  errorComponent: ConnectionError,
});

function AuthedLayout() {
  const { t } = useTranslation();
  const [menuOpen, setMenuOpen] = useState(false);

  return (
    <HubProvider>
      <div className="min-h-svh md:grid md:grid-cols-[16rem_minmax(0,1fr)]">
        {/* Wide screens: the sidebar stays in place while the page scrolls. */}
        <aside className="bg-sidebar sticky top-0 hidden h-svh border-r md:block">
          <SidebarContent />
        </aside>

        <div className="min-w-0">
          {/* Phones and narrow windows: a bar with the menu, which opens the same sidebar as a sheet. */}
          <header className="bg-background/95 sticky top-0 z-40 flex items-center gap-2 border-b px-2 py-2 backdrop-blur md:hidden">
            <Sheet open={menuOpen} onOpenChange={setMenuOpen}>
              <SheetTrigger asChild>
                <Button variant="ghost" size="icon" aria-label={t("layout.nav.menu")}>
                  <MenuIcon className="size-6" />
                </Button>
              </SheetTrigger>
              <SheetContent side="left" className="bg-sidebar w-72 gap-0 p-0" showCloseButton={false}>
                <SheetTitle className="sr-only">{t("layout.nav.menu")}</SheetTitle>
                <SheetDescription className="sr-only">{t("layout.nav.menuDescription")}</SheetDescription>
                <SidebarContent onNavigate={() => setMenuOpen(false)} />
              </SheetContent>
            </Sheet>
            <Link to="/" className="text-base">
              <Brand />
            </Link>
            <span className="ml-auto pr-2">
              <LiveIndicator />
            </span>
          </header>

          <main className="mx-auto w-full max-w-6xl p-4 sm:p-6 lg:p-8">
            <Outlet />
          </main>
        </div>
      </div>
    </HubProvider>
  );
}
