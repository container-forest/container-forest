import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { auditQuery, environmentsQuery, unmanagedStacksQuery } from "@/api/queries";
import { useUnmanagedStackActionMutation } from "@/api/unmanaged-stacks";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { ArrowLeftIcon, ArrowRightIcon, DownloadIcon, Loader2Icon, PlayIcon, RotateCwIcon, SquareIcon } from "@/components/icons";
import { StackContainersCard } from "@/components/stacks/stack-containers-card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { isKnownAction, isKnownChannel, outcomeVariant } from "@/features/audit/model";
import type { Environment } from "@/features/containers/model";
import { describeError } from "@/features/stacks/model";
import {
  UNMANAGED_HISTORY_SIZE,
  type UnmanagedStack,
  type UnmanagedStackAction,
  isUnmanagedActionAllowed,
  isUnmanagedActionEnabled,
  isUnmanagedStackEntry,
  needsUnmanagedConfirmation,
  shortConfigFiles,
  unmanagedHistoryQuery,
  unmanagedHistorySearch,
  unmanagedStackActions,
} from "@/features/unmanaged-stacks/model";
import { formatDateTime } from "@/lib/format";

export const Route = createFileRoute("/_authed/stacks/unmanaged/$environmentId/$stackName")({
  loader: ({ context }) => context.queryClient.ensureQueryData(environmentsQuery),
  component: UnmanagedStackScreen,
});

const icons = { up: PlayIcon, pull: DownloadIcon, restart: RotateCwIcon, down: SquareIcon } as const;

/**
 * One unmanaged stack (ADR-0016): its compose operations, its containers, where its files are, and for
 * administrators who ran what on it. The aggregator keeps nothing about it: everything comes from the agent's list.
 */
function UnmanagedStackScreen() {
  const { t } = useTranslation();
  const { environmentId, stackName } = Route.useParams();
  const { me } = Route.useRouteContext();
  const { data: environments } = useSuspenseQuery(environmentsQuery);
  const environment = environments.find((e) => e.id === environmentId);
  const stacks = useQuery({ ...unmanagedStacksQuery(environmentId), enabled: environment?.isOnline === true });
  const stack = stacks.data?.find((s) => s.name === stackName);

  return (
    <div className="mx-auto grid w-full max-w-4xl gap-6">
      <div className="grid gap-3">
        <Link
          to="/stacks"
          search={{ environment: environmentId }}
          className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm"
        >
          <ArrowLeftIcon className="size-4" />
          {t("stack.back")}
        </Link>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="font-heading text-2xl font-semibold break-all">{stackName}</h1>
          <Badge variant="secondary">{t("unmanagedStack.badge")}</Badge>
          {stack?.isSystem && <Badge variant="outline">{t("stacks.system")}</Badge>}
        </div>
        {environment && <p className="text-muted-foreground text-sm">{t("unmanagedStack.subtitle", { environment: environment.name })}</p>}
      </div>

      {environment === undefined ? (
        <ErrorText>{t("unmanagedStack.notFound")}</ErrorText>
      ) : !environment.isOnline ? (
        <p role="status" className="text-muted-foreground">
          {t("unmanagedStack.agentOffline")}
        </p>
      ) : stacks.isPending ? (
        <p className="text-muted-foreground flex items-center gap-2">
          <Loader2Icon className="animate-spin" />
          {t("unmanagedStack.loading")}
        </p>
      ) : stacks.isError ? (
        <ErrorText>
          {t("unmanagedStacks.loadFailed")} {t(describeError(stacks.error).key)} {describeError(stacks.error).detail}
        </ErrorText>
      ) : stack === undefined ? (
        // Its directory is gone or out of the roots, it became a managed stack, or the user has no role on the environment.
        <ErrorText>{t("unmanagedStack.notFound")}</ErrorText>
      ) : (
        <>
          <ActionsCard environment={environment} stack={stack} />
          <StackContainersCard environmentId={environment.id} stackName={stack.name} emptyText={t("unmanagedStack.noContainers")} />
          <FilesCard stack={stack} />
          {me.isAdmin && <HistoryCard environment={environment} stack={stack} />}
        </>
      )}
    </div>
  );
}

function ActionsCard({ environment, stack }: { environment: Environment; stack: UnmanagedStack }) {
  const { t } = useTranslation();
  const action = useUnmanagedStackActionMutation(environment.id);
  const allowed = unmanagedStackActions.filter((kind) => isUnmanagedActionAllowed(kind, stack));

  // A part of container-forest itself: no buttons, but the reason why.
  if (stack.isSystem) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>{t("stack.actions.title")}</CardTitle>
          <CardDescription>{t("unmanagedStacks.systemHint")}</CardDescription>
        </CardHeader>
      </Card>
    );
  }

  if (allowed.length === 0) {
    return null;
  }

  const run = (kind: UnmanagedStackAction) => action.mutate({ stack: stack.name, action: kind });
  const errorText = action.error ? describeError(action.error) : undefined;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.actions.title")}</CardTitle>
        <CardDescription>{t("unmanagedStack.actionsDescription")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="flex flex-wrap gap-2">
          {allowed.map((kind) => {
            const Icon = icons[kind];
            const label = t(`unmanagedStacks.actions.${kind}`);
            const icon =
              action.isPending && action.variables?.action === kind ? (
                <Loader2Icon data-icon="inline-start" className="animate-spin" />
              ) : (
                <Icon data-icon="inline-start" />
              );
            const disabled = action.isPending || !isUnmanagedActionEnabled(kind, stack);

            return needsUnmanagedConfirmation(kind) ? (
              <ConfirmAction
                key={kind}
                title={t(`unmanagedStacks.confirm.${kind as "restart" | "down"}Title`, { name: stack.name })}
                description={t(`unmanagedStacks.confirm.${kind as "restart" | "down"}Description`)}
                confirmLabel={label}
                disabled={disabled}
                onConfirm={() => run(kind)}
              >
                {icon}
                {label}
              </ConfirmAction>
            ) : (
              <Button key={kind} variant={kind === "up" ? "default" : "outline"} disabled={disabled} onClick={() => run(kind)}>
                {icon}
                {label}
              </Button>
            );
          })}
        </div>

        <div aria-live="polite" className="grid gap-2">
          {!stack.hasContainers && (
            <p className="text-muted-foreground text-sm">{t("unmanagedStack.nothingToStop")}</p>
          )}
          {action.isPending && (
            <p role="status" className="text-muted-foreground text-sm">
              {t("stack.actions.waiting")}
            </p>
          )}
          {errorText && (
            <ErrorText>
              {t(errorText.key)} {errorText.detail}
            </ErrorText>
          )}
          {action.isSuccess &&
            (action.data.succeeded ? (
              <p role="status" className="text-sm">
                {t(`unmanagedStacks.done.${action.variables.action}`, { name: stack.name })}
              </p>
            ) : (
              <ErrorText>
                {t("stack.actions.composeFailed")} {action.data.error}
              </ErrorText>
            ))}
          {action.isSuccess && action.data.output.length > 0 && (
            <pre className="bg-muted max-h-64 overflow-auto rounded-md p-3 font-mono text-xs whitespace-pre-wrap" lang="en">
              {action.data.output.join("\n")}
            </pre>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

/** Where the stack lives on the host: it is changed there, by hand, and brought up again from here. */
function FilesCard({ stack }: { stack: UnmanagedStack }) {
  const { t } = useTranslation();

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("unmanagedStack.files.title")}</CardTitle>
        <CardDescription>{t("unmanagedStack.files.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <dl className="grid gap-3 text-sm sm:grid-cols-[max-content_1fr] sm:gap-x-6">
          <dt className="text-muted-foreground">{t("unmanagedStack.files.directory")}</dt>
          <dd className="font-mono text-xs break-all sm:text-sm">{stack.directory}</dd>
          <dt className="text-muted-foreground">{t("unmanagedStack.files.composeFiles")}</dt>
          <dd>
            <ul className="grid gap-0.5 font-mono text-xs break-all sm:text-sm">
              {shortConfigFiles(stack).map((file) => (
                <li key={file}>{file}</li>
              ))}
            </ul>
          </dd>
          <dt className="text-muted-foreground">{t("unmanagedStack.files.foundBy")}</dt>
          <dd>{stack.hasContainers ? t("unmanagedStack.files.byLabels") : t("unmanagedStack.files.byDirectory")}</dd>
        </dl>
      </CardContent>
    </Card>
  );
}

/** Who ran what on the stack, from the audit log; the log is administrators' only, so is this card. */
function HistoryCard({ environment, stack }: { environment: Environment; stack: UnmanagedStack }) {
  const { t, i18n } = useTranslation();
  const history = useQuery(auditQuery(unmanagedHistoryQuery(environment.id, stack.name)));
  const entries = (history.data?.entries ?? []).filter((e) => isUnmanagedStackEntry(e, stack.name)).slice(0, UNMANAGED_HISTORY_SIZE);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("unmanagedStack.history.title")}</CardTitle>
        <CardDescription>{t("unmanagedStack.history.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {history.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("unmanagedStack.history.loading")}
          </p>
        ) : history.isError ? (
          <ErrorText>{t("unmanagedStack.history.loadFailed")}</ErrorText>
        ) : entries.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("unmanagedStack.history.empty")}</p>
        ) : (
          <ul className="grid gap-2">
            {entries.map((entry) => (
              <li key={entry.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                <Badge variant={outcomeVariant(entry.outcome)}>{t(`audit.outcomes.${entry.outcome}`)}</Badge>
                <span>{isKnownAction(entry.action) ? t(`audit.actions.${entry.action}`) : entry.action}</span>
                <span className="text-muted-foreground">
                  {entry.actorName ?? t("audit.unknownActor")}
                  {" · "}
                  {isKnownChannel(entry.channel) ? t(`audit.channels.${entry.channel}`) : entry.channel}
                  {" · "}
                  {formatDateTime(entry.occurredAt, i18n.resolvedLanguage)}
                </span>
                {entry.details && (
                  // Written by the aggregator in English (why it failed or was refused); not translated.
                  <span className="text-muted-foreground basis-full text-xs break-words" lang="en">
                    {entry.details}
                  </span>
                )}
              </li>
            ))}
          </ul>
        )}
        <Link
          to="/audit"
          search={unmanagedHistorySearch(environment.id, stack.name)}
          className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm"
        >
          {t("unmanagedStack.history.all")}
          <ArrowRightIcon className="size-4" />
        </Link>
      </CardContent>
    </Card>
  );
}
