import { useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { hasStatus } from "@/api/client";
import { usersQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { SearchIcon } from "@/components/icons";
import { useRememberListSearch } from "@/components/navigation/use-remember-list-search";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { CreateUserDialog } from "@/components/users/create-user-dialog";
import { UserKindBadge, UserStatusBadge, UserTwoFactorBadge } from "@/components/users/user-badges";
import { SignInSettingsCard } from "@/components/two-factor/sign-in-settings-card";
import { formatDate } from "@/lib/format";
import { rowClass, rowLinkClass } from "@/lib/row-link";
import { cn } from "@/lib/utils";
import { type UserStatusFilter, filterUsers, userStatusFilters } from "@/lib/users";

// Filters live in the URL, so a filtered list can be bookmarked, shared and restored with Back.
const searchSchema = z.object({
  q: z.string().optional().catch(undefined),
  status: z.enum(userStatusFilters).optional().catch(undefined),
});

export const Route = createFileRoute("/_authed/users/")({
  validateSearch: searchSchema,
  loader: ({ context }) => context.queryClient.ensureQueryData(usersQuery),
  component: UsersScreen,
  errorComponent: UsersLoadError,
});

/** Every user with their type and status; administrators only. */
function UsersScreen() {
  const { t, i18n } = useTranslation();
  const search = Route.useSearch();
  useRememberListSearch("users", search);
  const { q = "", status = "all" } = search;
  const navigate = useNavigate({ from: Route.fullPath });
  const { data: users } = useSuspenseQuery(usersQuery);
  const now = new Date();
  const shown = filterUsers(users, q, status, now);

  // Defaults are left out of the URL; replace keeps typing out of the history.
  const setFilters = (next: { q?: string; status?: UserStatusFilter }) =>
    navigate({
      search: (previous) => {
        const merged = { ...previous, ...next };
        return { q: merged.q || undefined, status: merged.status === "all" ? undefined : merged.status };
      },
      replace: true,
    });

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{t("users.title")}</h1>
          <p className="text-muted-foreground mt-1">{t("users.description")}</p>
        </div>
        <CreateUserDialog />
      </div>

      <div className="flex flex-wrap items-end gap-4">
        <div className="grid gap-2">
          <Label htmlFor="users-search">{t("users.search")}</Label>
          <div className="relative">
            <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
            <Input
              id="users-search"
              type="search"
              className="w-64 pl-8"
              value={q}
              onChange={(event) => setFilters({ q: event.target.value })}
            />
          </div>
        </div>
        <div className="grid gap-2">
          <Label htmlFor="users-status">{t("users.statusFilter")}</Label>
          <Select value={status} onValueChange={(value) => setFilters({ status: value as UserStatusFilter })}>
            <SelectTrigger id="users-status" className="w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {userStatusFilters.map((filter) => (
                <SelectItem key={filter} value={filter}>
                  {t(`users.filters.${filter}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </div>

      <SignInSettingsCard />

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("users.columns.userName")}</TableHead>
            <TableHead>{t("users.columns.kind")}</TableHead>
            <TableHead>{t("users.columns.status")}</TableHead>
            <TableHead className="hidden sm:table-cell">{t("users.columns.createdAt")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {shown.length === 0 ? (
            <TableRow>
              <TableCell colSpan={4} className="text-muted-foreground py-6 text-center">
                {t("users.empty")}
              </TableCell>
            </TableRow>
          ) : (
            shown.map((user) => (
              <TableRow key={user.id} className={rowClass}>
                <TableCell>
                  <Link to="/users/$userId" params={{ userId: user.id }} className={cn("font-medium", rowLinkClass)}>
                    {user.userName}
                  </Link>
                </TableCell>
                <TableCell>
                  <div className="flex flex-wrap gap-1">
                    <UserKindBadge user={user} />
                    <UserTwoFactorBadge user={user} />
                  </div>
                </TableCell>
                <TableCell>
                  <UserStatusBadge user={user} now={now} />
                </TableCell>
                <TableCell className="text-muted-foreground hidden sm:table-cell">
                  {formatDate(user.createdAt, i18n.resolvedLanguage)}
                </TableCell>
              </TableRow>
            ))
          )}
        </TableBody>
      </Table>
    </div>
  );
}

function UsersLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();

  return <ErrorText>{hasStatus(error, 403) ? t("users.forbidden") : t("users.loadFailed")}</ErrorText>;
}
