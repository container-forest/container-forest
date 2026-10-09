import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate, useParams, useRouterState, useSearch } from "@tanstack/react-router";
import { type ComponentType, type ReactNode, type SVGProps, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { useLogoutMutation } from "@/api/auth";
import { environmentsQuery, meQuery, stackQuery } from "@/api/queries";
import {
  BellIcon,
  BoxIcon,
  FolderGitIcon,
  KeyIcon,
  LayersIcon,
  LogOutIcon,
  MessageCircleIcon,
  ServerIcon,
  TokenIcon,
  SettingsIcon,
  UserIcon,
  UsersIcon,
} from "@/components/icons";
import { LanguageSwitcher } from "@/components/language-switcher";
import { LiveIndicator } from "@/components/live/live-indicator";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { Environment } from "@/features/containers/model";
import {
  type EnvironmentSection,
  chooseEnvironment,
  environmentOfPage,
  readRememberedEnvironment,
  rememberEnvironment,
  sectionOf,
} from "@/features/navigation/model";
import { cn } from "@/lib/utils";

/**
 * The environment the sidebar works with and the environment section that is open. The page decides when it is
 * about an environment; otherwise the one the user worked with last stays selected.
 */
function useCurrentEnvironment() {
  const { data: environments = [] } = useQuery(environmentsQuery);
  const pathname = useRouterState({ select: (state) => state.location.pathname });
  const params = useParams({ strict: false }) as { environmentId?: string; stackId?: string };
  const search = useSearch({ strict: false }) as { environment?: string };
  // The stack page has the stack loaded already; its environment is the page's.
  const stack = useQuery({ ...stackQuery(params.stackId ?? ""), enabled: params.stackId !== undefined });

  const fromPage = environmentOfPage({
    environmentIdParam: params.environmentId,
    environmentSearch: search.environment,
    stackEnvironmentId: params.stackId === undefined ? undefined : stack.data?.environmentId,
  });
  const id = chooseEnvironment(environments, fromPage, readRememberedEnvironment());

  useEffect(() => {
    if (fromPage !== undefined && fromPage === id) {
      rememberEnvironment(fromPage);
    }
  }, [fromPage, id]);

  return { environments, environment: environments.find((e) => e.id === id), section: sectionOf(pathname), pathname };
}

const itemClass =
  "flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-colors outline-none focus-visible:ring-2 focus-visible:ring-sidebar-ring [&>svg]:size-5 [&>svg]:shrink-0";
const activeClass = "bg-sidebar-accent text-sidebar-accent-foreground font-semibold";
const inactiveClass = "text-sidebar-foreground/75 hover:bg-sidebar-accent/60 hover:text-sidebar-accent-foreground";

/** A sidebar entry. Which one is active is decided here from the path, not by the router's link matching. */
function NavItem({
  active,
  icon: Icon,
  label,
  children,
}: {
  active: boolean;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  label: string;
  children: (props: { className: string; "aria-current": "page" | undefined; children: ReactNode }) => ReactNode;
}) {
  return children({
    className: cn(itemClass, active ? activeClass : inactiveClass),
    "aria-current": active ? "page" : undefined,
    children: (
      <>
        <Icon aria-hidden />
        <span className="truncate">{label}</span>
      </>
    ),
  });
}

function NavGroup({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <div className="grid gap-1">
      <p className="text-muted-foreground px-3 text-xs font-semibold tracking-wider uppercase">{label}</p>
      <nav className="grid gap-0.5">{children}</nav>
    </div>
  );
}

/**
 * Picks the environment the "Environment" group works on. Switching keeps the open section, so on the containers
 * of one environment it opens the containers of the other.
 */
function EnvironmentSwitcher({
  environments,
  environment,
  section,
  onNavigate,
}: {
  environments: Environment[];
  environment: Environment | undefined;
  section: EnvironmentSection | undefined;
  onNavigate: () => void;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();

  const open = (id: string) => {
    rememberEnvironment(id);
    onNavigate();
    switch (section) {
      case "containers":
        return navigate({ to: "/containers", search: { environment: id } });
      case "keys":
        return navigate({ to: "/keys", search: { environment: id } });
      case "settings":
        return navigate({ to: "/environments/$environmentId", params: { environmentId: id } });
      default:
        return navigate({ to: "/stacks", search: { environment: id } });
    }
  };

  return (
    <div className="grid gap-1.5 px-1">
      <label htmlFor="sidebar-environment" className="text-muted-foreground px-2 text-xs font-semibold tracking-wider uppercase">
        {t("layout.nav.environment")}
      </label>
      <Select
        value={environment?.id ?? ""}
        // Radix reports "" while the value matches no item; only a real choice of another environment navigates.
        onValueChange={(id) => id !== "" && id !== environment?.id && void open(id)}
      >
        <SelectTrigger id="sidebar-environment" className="bg-background w-full">
          <SelectValue placeholder={t("layout.nav.chooseEnvironment")} />
        </SelectTrigger>
        <SelectContent>
          {environments.map((e) => (
            <SelectItem key={e.id} value={e.id}>
              <span
                aria-hidden
                className={cn("size-2 shrink-0 rounded-full", e.isOnline ? "bg-emerald-500" : "bg-muted-foreground/50")}
              />
              {e.name}
              {!e.isOnline && <span className="sr-only">{t("layout.nav.offline")}</span>}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}

/**
 * Everything the user can open, grouped by what it is about: the chosen environment, the whole installation
 * (administration) and the user's own settings. The same content fills the fixed sidebar on wide screens and the
 * menu sheet on phones; `onNavigate` lets the sheet close after a choice.
 */
export function SidebarContent({ onNavigate = () => {} }: { onNavigate?: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { data: me } = useQuery(meQuery);
  const logout = useLogoutMutation();
  const { environments, environment, section, pathname } = useCurrentEnvironment();

  const isAdmin = me?.isAdmin ?? false;
  // Repositories are for administrators and editors; hiding the link is a convenience, the aggregator decides.
  const canSeeRepositories = isAdmin || environments.some((e) => e.role === "Editor");
  const at = (...prefixes: string[]) => prefixes.some((p) => pathname === p || pathname.startsWith(`${p}/`));

  return (
    // In development the Query devtools button sits in the bottom-left corner; the language switch stays above it.
    <div className={cn("flex h-full flex-col gap-6 overflow-y-auto p-3", import.meta.env.DEV && "pb-16")}>
      <div className="flex items-center justify-between gap-2 px-2 pt-1">
        <Link to="/" onClick={onNavigate} className="font-heading text-lg font-extrabold tracking-tight">
          container<span className="text-emerald-500">-</span>forest
        </Link>
        <LiveIndicator />
      </div>

      {environments.length === 0 ? (
        <p className="text-muted-foreground px-3 text-sm">{t("layout.nav.noEnvironments")}</p>
      ) : (
        <div className="grid gap-3">
          <EnvironmentSwitcher
            environments={environments}
            environment={environment}
            section={section}
            onNavigate={onNavigate}
          />
          {environment && (
            <nav className="grid gap-0.5" aria-label={t("layout.nav.environment")}>
              <NavItem active={section === "stacks"} icon={LayersIcon} label={t("layout.nav.stacks")}>
                {(props) => <Link to="/stacks" search={{ environment: environment.id }} onClick={onNavigate} {...props} />}
              </NavItem>
              <NavItem active={section === "containers"} icon={BoxIcon} label={t("layout.nav.containers")}>
                {(props) => <Link to="/containers" search={{ environment: environment.id }} onClick={onNavigate} {...props} />}
              </NavItem>
              <NavItem active={section === "keys"} icon={KeyIcon} label={t("layout.nav.keys")}>
                {(props) => <Link to="/keys" search={{ environment: environment.id }} onClick={onNavigate} {...props} />}
              </NavItem>
              <NavItem active={section === "settings"} icon={SettingsIcon} label={t("layout.nav.settings")}>
                {(props) => (
                  <Link
                    to="/environments/$environmentId"
                    params={{ environmentId: environment.id }}
                    onClick={onNavigate}
                    {...props}
                  />
                )}
              </NavItem>
            </nav>
          )}
        </div>
      )}

      {(isAdmin || canSeeRepositories) && (
        <NavGroup label={t("layout.nav.administration")}>
          {isAdmin && (
            <NavItem active={pathname === "/environments" || pathname === "/environments/"} icon={ServerIcon} label={t("layout.nav.environments")}>
              {(props) => <Link to="/environments" onClick={onNavigate} {...props} />}
            </NavItem>
          )}
          {canSeeRepositories && (
            <NavItem active={at("/repositories", "/git-connections")} icon={FolderGitIcon} label={t("layout.nav.repositories")}>
              {(props) => <Link to="/repositories" onClick={onNavigate} {...props} />}
            </NavItem>
          )}
          {isAdmin && (
            <NavItem active={at("/users")} icon={UsersIcon} label={t("layout.nav.users")}>
              {(props) => <Link to="/users" onClick={onNavigate} {...props} />}
            </NavItem>
          )}
          {isAdmin && (
            <NavItem active={at("/notifications")} icon={BellIcon} label={t("layout.nav.notifications")}>
              {(props) => <Link to="/notifications" onClick={onNavigate} {...props} />}
            </NavItem>
          )}
        </NavGroup>
      )}

      <div className="mt-auto grid gap-3">
        <NavGroup label={t("layout.nav.mine")}>
          <NavItem active={at("/account")} icon={UserIcon} label={me?.userName ?? t("layout.nav.account")}>
            {(props) => <Link to="/account" onClick={onNavigate} title={t("layout.nav.account")} {...props} />}
          </NavItem>
          <NavItem active={at("/discord")} icon={MessageCircleIcon} label={t("layout.nav.discord")}>
            {(props) => <Link to="/discord" onClick={onNavigate} {...props} />}
          </NavItem>
          <NavItem active={at("/tokens")} icon={TokenIcon} label={t("layout.nav.tokens")}>
            {(props) => <Link to="/tokens" onClick={onNavigate} {...props} />}
          </NavItem>
        </NavGroup>
        <div className="flex items-center justify-between gap-2 px-1">
          <LanguageSwitcher />
          <Button
            variant="ghost"
            size="sm"
            disabled={logout.isPending}
            onClick={() =>
              logout.mutate(undefined, {
                onSuccess: () => {
                  onNavigate();
                  void navigate({ to: "/login" });
                },
              })
            }
          >
            <LogOutIcon data-icon="inline-start" />
            {t("layout.signOut")}
          </Button>
        </div>
      </div>
    </div>
  );
}
