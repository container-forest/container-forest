import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { type FormEvent, useState } from "react";
import { useTranslation } from "react-i18next";
import { auditQuery, environmentsQuery, usersQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { ChevronLeftIcon, ChevronRightIcon, FilterXIcon, Loader2Icon, RefreshCwIcon, SearchIcon } from "@/components/icons";
import { useRememberListSearch } from "@/components/navigation/use-remember-list-search";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  AUDIT_PAGE_SIZE,
  type AuditEntry,
  type AuditSearch,
  auditCategories,
  auditOutcomes,
  auditQueryOf,
  auditSearchSchema,
  isFiltered,
  isKnownAction,
  isKnownChannel,
  outcomeVariant,
  pageRange,
  targetLink,
} from "@/features/audit/model";
import { describeError } from "@/features/stacks/model";
import { formatTimestamp } from "@/lib/format";

/** Radix Select cannot hold an empty value; "all" stands for "no filter", which the URL leaves out. */
const ALL = "all";

export const Route = createFileRoute("/_authed/audit")({
  validateSearch: auditSearchSchema,
  loaderDeps: ({ search }) => auditQueryOf(search),
  loader: ({ context, deps }) => context.queryClient.ensureQueryData(auditQuery(deps)),
  component: AuditScreen,
  errorComponent: AuditLoadError,
});

/** The audit log for administrators: who did what, filtered by area, outcome, user, environment, name and days. */
function AuditScreen() {
  const { t } = useTranslation();
  const search = Route.useSearch();
  useRememberListSearch("audit", search);
  const navigate = Route.useNavigate();
  // The previous page stays on screen while the next one loads, so the table does not jump.
  const log = useQuery({ ...auditQuery(auditQueryOf(search)), placeholderData: keepPreviousData });

  // Any filter change starts again from the first page; replace keeps filter changes out of the history.
  const setFilters = (next: Partial<AuditSearch>) =>
    navigate({ search: (previous) => ({ ...previous, ...next, page: undefined }), replace: true });
  const setPage = (page: number) =>
    navigate({ search: (previous) => ({ ...previous, page: page > 1 ? page : undefined }) });

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="max-w-3xl">
          <h1 className="font-heading text-2xl font-semibold">{t("audit.title")}</h1>
          <p className="text-muted-foreground mt-1">{t("audit.description")}</p>
        </div>
        <Button variant="outline" disabled={log.isFetching} onClick={() => void log.refetch()}>
          {log.isFetching ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <RefreshCwIcon data-icon="inline-start" />}
          {t("audit.refresh")}
        </Button>
      </div>

      <AuditFilters search={search} setFilters={setFilters} />

      {log.isError && !log.data ? (
        <ErrorText>
          {t("audit.loadFailed")} {t(describeError(log.error).key)}
        </ErrorText>
      ) : !log.data ? (
        <p className="text-muted-foreground flex items-center gap-2">
          <Loader2Icon className="animate-spin" />
        </p>
      ) : log.data.entries.length === 0 ? (
        <p className="text-muted-foreground">{isFiltered(search) ? t("audit.emptyFiltered") : t("audit.empty")}</p>
      ) : (
        <>
          <AuditTable entries={log.data.entries} onActor={(user) => setFilters({ user })} />
          <Pager page={Number(log.data.page)} total={Number(log.data.total)} onPage={setPage} />
        </>
      )}
    </div>
  );
}

function AuditFilters({ search, setFilters }: { search: AuditSearch; setFilters: (next: Partial<AuditSearch>) => void }) {
  const { t } = useTranslation();
  // Names for the filters only: the log shows without them.
  const users = useQuery(usersQuery);
  const environments = useQuery(environmentsQuery);
  const [text, setText] = useState(search.q ?? "");

  // Searching asks the aggregator, so it runs on Enter or the button, not on every key.
  const onSearch = (event: FormEvent) => {
    event.preventDefault();
    setFilters({ q: text.trim() || undefined });
  };

  return (
    <div className="flex flex-wrap items-end gap-3">
      <div className="grid gap-1.5">
        <Label htmlFor="audit-category">{t("audit.filters.category")}</Label>
        <Select
          value={search.category ?? ALL}
          // Radix reports "" while the value matches no item; that is not a choice.
          onValueChange={(value) => value !== "" && setFilters({ category: auditCategories.find((c) => c === value) })}
        >
          <SelectTrigger id="audit-category" className="w-52">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>{t("audit.filters.allCategories")}</SelectItem>
            {auditCategories.map((category) => (
              <SelectItem key={category} value={category}>
                {t(`audit.categories.${category}`)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="audit-outcome">{t("audit.filters.outcome")}</Label>
        <Select
          value={search.outcome ?? ALL}
          onValueChange={(value) => value !== "" && setFilters({ outcome: auditOutcomes.find((o) => o === value) })}
        >
          <SelectTrigger id="audit-outcome" className="w-40">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>{t("audit.filters.allOutcomes")}</SelectItem>
            {auditOutcomes.map((outcome) => (
              <SelectItem key={outcome} value={outcome}>
                {t(`audit.outcomes.${outcome}`)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="audit-user">{t("audit.filters.user")}</Label>
        <Select
          value={search.user ?? ALL}
          onValueChange={(value) => value !== "" && setFilters({ user: value === ALL ? undefined : value })}
        >
          <SelectTrigger id="audit-user" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>{t("audit.filters.allUsers")}</SelectItem>
            {(users.data ?? []).map((user) => (
              <SelectItem key={user.id} value={user.id}>
                {user.userName}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="audit-environment">{t("audit.filters.environment")}</Label>
        <Select
          value={search.environment ?? ALL}
          onValueChange={(value) => value !== "" && setFilters({ environment: value === ALL ? undefined : value })}
        >
          <SelectTrigger id="audit-environment" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>{t("audit.filters.allEnvironments")}</SelectItem>
            {(environments.data ?? []).map((environment) => (
              <SelectItem key={environment.id} value={environment.id}>
                {environment.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="audit-from">{t("audit.filters.from")}</Label>
        <Input
          id="audit-from"
          type="date"
          className="w-40"
          value={search.from ?? ""}
          max={search.to}
          onChange={(event) => setFilters({ from: event.target.value || undefined })}
        />
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="audit-to">{t("audit.filters.to")}</Label>
        <Input
          id="audit-to"
          type="date"
          className="w-40"
          value={search.to ?? ""}
          min={search.from}
          onChange={(event) => setFilters({ to: event.target.value || undefined })}
        />
      </div>

      <form className="grid min-w-64 flex-1 gap-1.5" onSubmit={onSearch} role="search">
        <Label htmlFor="audit-search">{t("audit.filters.search")}</Label>
        <div className="flex gap-2">
          <div className="relative flex-1">
            <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
            <Input
              id="audit-search"
              type="search"
              className="pl-8"
              maxLength={100}
              placeholder={t("audit.filters.searchPlaceholder")}
              value={text}
              onChange={(event) => setText(event.target.value)}
            />
          </div>
          <Button type="submit" variant="secondary">
            {t("audit.filters.apply")}
          </Button>
        </div>
      </form>

      {isFiltered(search) && (
        <Button
          variant="ghost"
          onClick={() => {
            setText("");
            setFilters({
              category: undefined,
              outcome: undefined,
              user: undefined,
              environment: undefined,
              q: undefined,
              from: undefined,
              to: undefined,
            });
          }}
        >
          <FilterXIcon data-icon="inline-start" />
          {t("audit.filters.clear")}
        </Button>
      )}
    </div>
  );
}

function AuditTable({ entries, onActor }: { entries: readonly AuditEntry[]; onActor: (userId: string) => void }) {
  const { t, i18n } = useTranslation();

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead className="hidden sm:table-cell">{t("audit.columns.occurred")}</TableHead>
          <TableHead>{t("audit.columns.actor")}</TableHead>
          <TableHead>{t("audit.columns.action")}</TableHead>
          <TableHead className="hidden md:table-cell">{t("audit.columns.target")}</TableHead>
          <TableHead>{t("audit.columns.outcome")}</TableHead>
          <TableHead className="hidden lg:table-cell">{t("audit.columns.details")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {entries.map((entry) => (
          <TableRow key={entry.id} className="align-top">
            <TableCell className="hidden text-sm whitespace-nowrap sm:table-cell">
              {formatTimestamp(entry.occurredAt, i18n.resolvedLanguage)}
            </TableCell>
            <TableCell className="text-sm whitespace-normal">
              {entry.actorId ? (
                <button
                  type="button"
                  className="font-medium hover:underline focus-visible:underline focus-visible:outline-none"
                  title={t("audit.filterByUser", { name: entry.actorName ?? entry.actorId })}
                  onClick={() => onActor(entry.actorId!)}
                >
                  {entry.actorName ?? entry.actorId}
                </button>
              ) : (
                <span className="font-medium">{entry.actorName ?? t("audit.unknownActor")}</span>
              )}
              <div className="text-muted-foreground text-xs">
                {isKnownChannel(entry.channel) ? t(`audit.channels.${entry.channel}`) : entry.channel}
              </div>
              {entry.address && <div className="text-muted-foreground font-mono text-xs break-all">{entry.address}</div>}
            </TableCell>
            <TableCell className="text-sm whitespace-normal">
              <div>{isKnownAction(entry.action) ? t(`audit.actions.${entry.action}`) : entry.action}</div>
              {/* On narrow screens the time and object columns are hidden; they go under the action. */}
              <div className="text-muted-foreground text-xs md:hidden">
                <AuditTarget entry={entry} />
              </div>
              <div className="text-muted-foreground text-xs sm:hidden">{formatTimestamp(entry.occurredAt, i18n.resolvedLanguage)}</div>
            </TableCell>
            <TableCell className="hidden text-sm whitespace-normal md:table-cell">
              <AuditTarget entry={entry} />
            </TableCell>
            <TableCell>
              <Badge variant={outcomeVariant(entry.outcome)}>{t(`audit.outcomes.${entry.outcome}`)}</Badge>
              {entry.details && (
                // Written by the aggregator in English (e.g. why it was refused); not translated.
                <p className="text-muted-foreground mt-1 max-w-56 text-xs break-words whitespace-normal lg:hidden" lang="en">
                  {entry.details}
                </p>
              )}
            </TableCell>
            <TableCell className="text-muted-foreground hidden max-w-md min-w-48 text-xs break-words whitespace-normal lg:table-cell" lang="en">
              {entry.details}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** The target's name, linked to its page when it has one. */
function AuditTarget({ entry }: { entry: AuditEntry }) {
  const link = targetLink(entry);
  const name = entry.targetName ?? entry.targetId;
  if (!name) {
    return null;
  }

  const kind = entry.targetKind === "File" ? "font-mono break-all" : "break-words";
  return link ? (
    <Link {...link} className={`${kind} font-medium hover:underline`}>
      {name}
    </Link>
  ) : (
    <span className={kind}>{name}</span>
  );
}

function Pager({ page, total, onPage }: { page: number; total: number; onPage: (page: number) => void }) {
  const { t } = useTranslation();
  const range = pageRange(page, AUDIT_PAGE_SIZE, total);

  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <p className="text-muted-foreground text-sm">{t("audit.range", { ...range, total })}</p>
      <div className="flex gap-2">
        <Button variant="outline" size="sm" disabled={!range.hasPrevious} onClick={() => onPage(page - 1)}>
          <ChevronLeftIcon data-icon="inline-start" />
          {t("audit.previous")}
        </Button>
        <Button variant="outline" size="sm" disabled={!range.hasNext} onClick={() => onPage(page + 1)}>
          {t("audit.next")}
          <ChevronRightIcon data-icon="inline-end" />
        </Button>
      </div>
    </div>
  );
}

function AuditLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();
  const { key, detail } = describeError(error);

  return (
    <ErrorText>
      {t("audit.loadFailed")} {t(key)} {detail}
    </ErrorText>
  );
}
