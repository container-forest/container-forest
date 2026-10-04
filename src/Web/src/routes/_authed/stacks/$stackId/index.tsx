import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import { deploymentsQuery, repositoriesQuery, stackQuery } from "@/api/queries";
import {
  useChangeStackSourceMutation,
  useDeployStackMutation,
  useSetDeployOnPushMutation,
  useSetRestartOnFileChangeMutation,
  useStackActionMutation,
} from "@/api/stacks";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText, FieldError } from "@/components/form-messages";
import { ArrowLeftIcon, ArrowRightIcon, Loader2Icon, PlayIcon, RefreshCwIcon, RotateCwIcon, SquareIcon } from "@/components/icons";
import { DeployHookCard } from "@/components/stacks/deploy-hook-card";
import { DeploymentStatusBadge } from "@/components/stacks/deployment-status-badge";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import {
  DEPLOYMENT_POLL_MS,
  type Stack,
  type StackAction,
  type StackCommandResult,
  type StackSourceForm,
  describeError,
  hasUnfinished,
  receivesPushes,
  shortCommit,
  stackSourceSchema,
} from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";
import { useLiveStatus } from "@/components/live/hub-provider";
import { pollingInterval } from "@/features/live/model";

/** How many deployments the stack's screen lists; the history screen has the rest. */
const RECENT_DEPLOYMENTS = 5;

export const Route = createFileRoute("/_authed/stacks/$stackId/")({
  loader: ({ context, params }) => context.queryClient.ensureQueryData(stackQuery(params.stackId)),
  component: StackScreen,
  errorComponent: StackLoadError,
});

/** One stack: deploy, restart and take down; its source (the stack editor); its latest deployments. */
function StackScreen() {
  const { t, i18n } = useTranslation();
  const { stackId } = Route.useParams();
  const { data: stack } = useSuspenseQuery(stackQuery(stackId));

  return (
    <div className="grid max-w-4xl gap-6">
      <div className="grid gap-3">
        <Link
          to="/stacks"
          search={{ environment: stack.environmentId }}
          className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm"
        >
          <ArrowLeftIcon className="size-4" />
          {t("stack.back")}
        </Link>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="font-heading text-2xl font-semibold">{stack.name}</h1>
          {stack.isSystem && <Badge variant="outline">{t("stacks.system")}</Badge>}
        </div>
        <p className="text-muted-foreground text-sm">
          {stack.deployedCommit && stack.deployedAt
            ? t("stack.deployedAt", { commit: shortCommit(stack.deployedCommit), date: formatDateTime(stack.deployedAt, i18n.resolvedLanguage) })
            : t("stacks.notDeployed")}
        </p>
      </div>

      <ActionsCard stack={stack} />
      <SourceCard key={`${stack.repositoryId}/${stack.branch}/${stack.composePath}`} stack={stack} />
      {stack.canDeploy && !stack.isSystem && <DeployOnPushCard stack={stack} />}
      {stack.canDeploy && !stack.isSystem && <RestartOnFileChangeCard stack={stack} />}
      {stack.canDeploy && !stack.isSystem && <DeployHookCard stack={stack} />}
      <RecentDeploymentsCard stack={stack} />
    </div>
  );
}

function ActionsCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const deployments = useQuery(deploymentsQuery(stack.id));
  const deploy = useDeployStackMutation(stack.id);
  const action = useStackActionMutation(stack.id);
  const [result, setResult] = useState<{ action: StackAction; result: StackCommandResult }>();

  const busy = deploy.isPending || action.isPending;
  // The aggregator answers 409 while a deployment is unfinished; the buttons say so before the click.
  const deploying = deployments.data !== undefined && hasUnfinished(deployments.data);

  const onDeploy = (pull: boolean) => {
    setResult(undefined);
    action.reset();
    deploy.mutate(pull, {
      onSuccess: (deployment) => void navigate({ to: "/deployments/$deploymentId", params: { deploymentId: deployment.id } }),
    });
  };

  const onAction = (kind: StackAction) => {
    setResult(undefined);
    deploy.reset();
    action.mutate({ action: kind }, { onSuccess: (outcome) => setResult({ action: kind, result: outcome }) });
  };

  const error = deploy.error ?? action.error;
  const errorText = error ? describeError(error) : undefined;

  if (!stack.canDeploy && !stack.canRestart) {
    return null;
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.actions.title")}</CardTitle>
        <CardDescription>{stack.isSystem ? t("stack.actions.systemHint") : t("stack.actions.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="flex flex-wrap gap-2">
          {stack.canDeploy && (
            <>
              <Button disabled={busy || deploying || stack.isSystem} onClick={() => onDeploy(false)}>
                {deploy.isPending && deploy.variables === false ? (
                  <Loader2Icon data-icon="inline-start" className="animate-spin" />
                ) : (
                  <PlayIcon data-icon="inline-start" />
                )}
                {t("stack.actions.deploy")}
              </Button>
              <Button variant="outline" disabled={busy || deploying || stack.isSystem} onClick={() => onDeploy(true)}>
                {deploy.isPending && deploy.variables === true ? (
                  <Loader2Icon data-icon="inline-start" className="animate-spin" />
                ) : (
                  <RefreshCwIcon data-icon="inline-start" />
                )}
                {t("stack.actions.deployWithPull")}
              </Button>
            </>
          )}
          {stack.canRestart && (
            <ConfirmAction
              title={t("stack.actions.confirmRestart.title", { name: stack.name })}
              description={t("stack.actions.confirmRestart.description")}
              confirmLabel={t("stack.actions.restart")}
              disabled={busy || deploying || stack.isSystem || stack.deployedCommit === null}
              onConfirm={() => onAction("restart")}
            >
              {action.isPending && action.variables?.action === "restart" ? (
                <Loader2Icon data-icon="inline-start" className="animate-spin" />
              ) : (
                <RotateCwIcon data-icon="inline-start" />
              )}
              {t("stack.actions.restart")}
            </ConfirmAction>
          )}
          {stack.canDeploy && (
            <ConfirmAction
              title={t("stack.actions.confirmDown.title", { name: stack.name })}
              description={t("stack.actions.confirmDown.description")}
              confirmLabel={t("stack.actions.down")}
              disabled={busy || deploying || stack.isSystem || stack.deployedCommit === null}
              onConfirm={() => onAction("down")}
            >
              {action.isPending && action.variables?.action === "down" ? (
                <Loader2Icon data-icon="inline-start" className="animate-spin" />
              ) : (
                <SquareIcon data-icon="inline-start" />
              )}
              {t("stack.actions.down")}
            </ConfirmAction>
          )}
        </div>

        <div aria-live="polite" className="grid gap-2">
          {deploying && (
            <p role="status" className="text-muted-foreground text-sm">
              {t("stack.actions.deploymentRunning")}
            </p>
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
          {result && <CommandOutput action={result.action} result={result.result} />}
        </div>
      </CardContent>
    </Card>
  );
}

/** What compose printed for a restart or down; a failure is a result too, with the reason. */
function CommandOutput({ action, result }: { action: StackAction; result: StackCommandResult }) {
  const { t } = useTranslation();

  return (
    <div className="grid gap-2">
      {result.succeeded ? (
        <p role="status" className="text-sm">
          {t(`stack.actions.done.${action}`)}
        </p>
      ) : (
        <ErrorText>
          {t("stack.actions.composeFailed")} {result.error}
        </ErrorText>
      )}
      {result.output.length > 0 && (
        <pre className="bg-muted max-h-64 overflow-auto rounded-md p-3 font-mono text-xs whitespace-pre-wrap" lang="en">
          {result.output.join("\n")}
        </pre>
      )}
    </div>
  );
}

/** The stack editor: repository, branch and compose file; editors only. Keyed by the source, so a save starts over. */
function SourceCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const repositories = useQuery({ ...repositoriesQuery, enabled: stack.canDeploy });
  const change = useChangeStackSourceMutation(stack.id);
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors, isDirty },
  } = useForm<StackSourceForm>({
    resolver: zodResolver(stackSourceSchema),
    defaultValues: { repositoryId: stack.repositoryId, branch: stack.branch, composePath: stack.composePath },
  });

  const onSubmit = handleSubmit((form) => change.mutate(form));
  const changeError = change.error ? describeError(change.error) : undefined;

  if (!stack.canDeploy) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>{t("stack.source.title")}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-1 text-sm">
          <div>{stack.repositoryName ?? t("stacks.repositoryGone")}</div>
          <div className="text-muted-foreground font-mono text-xs">
            {stack.branch} · {stack.composePath}
          </div>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.source.title")}</CardTitle>
        <CardDescription>{t("stack.source.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <div className="grid gap-1.5">
            <Label htmlFor="source-repository">{t("stacks.form.repository")}</Label>
            <Controller
              control={control}
              name="repositoryId"
              render={({ field }) => (
                <Select value={field.value} onValueChange={(value) => value !== "" && field.onChange(value)}>
                  <SelectTrigger id="source-repository" className="max-w-sm">
                    <SelectValue placeholder={stack.repositoryName ?? t("stacks.repositoryGone")} />
                  </SelectTrigger>
                  <SelectContent>
                    {(repositories.data ?? []).map((repository) => (
                      <SelectItem key={repository.id} value={repository.id}>
                        {/* Names need not be unique (ADR-0006): the address tells them apart. */}
                        {repository.name} — {repository.url}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            />
            <FieldError id="source-repository-error" message={errors.repositoryId?.message} />
            {repositories.isError && <ErrorText>{t("stacks.form.repositoriesFailed")}</ErrorText>}
          </div>
          <div className="grid items-start gap-3 sm:grid-cols-2">
            <div className="grid gap-1.5">
              <Label htmlFor="source-branch">{t("stacks.form.branch")}</Label>
              <Input
                id="source-branch"
                autoComplete="off"
                aria-invalid={errors.branch ? true : undefined}
                aria-describedby={errors.branch ? "source-branch-error" : undefined}
                {...register("branch")}
              />
              <FieldError id="source-branch-error" message={errors.branch?.message} />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="source-compose-path">{t("stacks.form.composePath")}</Label>
              <Input
                id="source-compose-path"
                autoComplete="off"
                className="font-mono"
                aria-invalid={errors.composePath ? true : undefined}
                aria-describedby={errors.composePath ? "source-compose-path-error" : "source-compose-path-hint"}
                {...register("composePath")}
              />
              {errors.composePath ? (
                <FieldError id="source-compose-path-error" message={errors.composePath.message} />
              ) : (
                <p id="source-compose-path-hint" className="text-muted-foreground text-sm">
                  {t("stacks.form.composePathHint")}
                </p>
              )}
            </div>
          </div>
          {changeError && (
            <ErrorText>
              {t(changeError.key)} {changeError.detail}
            </ErrorText>
          )}
          {change.isSuccess && !isDirty && (
            <p role="status" className="text-sm">
              {t("stack.source.saved")}
            </p>
          )}
          <div className="flex gap-2">
            <Button type="submit" disabled={!isDirty || change.isPending}>
              {change.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("stack.source.save")}
            </Button>
            <Button
              type="button"
              variant="ghost"
              disabled={!isDirty || change.isPending}
              onClick={() => {
                reset();
                change.reset();
              }}
            >
              {t("stack.source.discard")}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}

/** Deploying on push: the stack's switch; the repository's webhook is the administrator's (repositories screen). */
function DeployOnPushCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const repositories = useQuery(repositoriesQuery);
  const change = useSetDeployOnPushMutation(stack.id);
  const repository = repositories.data?.find((r) => r.id === stack.repositoryId);
  const changeError = change.error ? describeError(change.error) : undefined;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.push.title")}</CardTitle>
        <CardDescription>{t("stack.push.description", { branch: stack.branch })}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <div className="flex items-center gap-3">
          <Switch
            id="deploy-on-push"
            checked={change.isPending ? change.variables : stack.deployOnPush}
            disabled={change.isPending}
            onCheckedChange={(enabled) => change.mutate(enabled)}
          />
          <Label htmlFor="deploy-on-push">{t("stack.push.label")}</Label>
        </div>
        <div aria-live="polite">
          {stack.deployOnPush && repository !== undefined && !receivesPushes(repository) && (
            <p className="text-muted-foreground text-sm">{t("stack.push.noWebhook")}</p>
          )}
          {changeError && (
            <ErrorText>
              {t("stack.push.failed")} {t(changeError.key)} {changeError.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

function RestartOnFileChangeCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const change = useSetRestartOnFileChangeMutation(stack.id);
  const changeError = change.error ? describeError(change.error) : undefined;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.restartOnFileChange.title")}</CardTitle>
        <CardDescription>{t("stack.restartOnFileChange.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <div className="flex items-center gap-3">
          <Switch
            id="restart-on-file-change"
            checked={change.isPending ? change.variables : stack.restartOnFileChange}
            disabled={change.isPending}
            onCheckedChange={(enabled) => change.mutate(enabled)}
          />
          <Label htmlFor="restart-on-file-change">{t("stack.restartOnFileChange.label")}</Label>
        </div>
        <div aria-live="polite">
          {changeError && (
            <ErrorText>
              {t("stack.restartOnFileChange.failed")} {t(changeError.key)} {changeError.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

function RecentDeploymentsCard({ stack }: { stack: Stack }) {
  const { t, i18n } = useTranslation();
  const live = useLiveStatus();
  const deployments = useQuery({
    ...deploymentsQuery(stack.id),
    // A queued or running deployment changes on its own; live updates follow it, polling only without them.
    refetchInterval: (query) =>
      query.state.data && hasUnfinished(query.state.data) ? pollingInterval(live, DEPLOYMENT_POLL_MS) : false,
  });
  const recent = (deployments.data ?? []).slice(0, RECENT_DEPLOYMENTS);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.recent.title")}</CardTitle>
      </CardHeader>
      <CardContent className="grid gap-3">
        {deployments.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("deployments.loading")}
          </p>
        ) : deployments.isError ? (
          <ErrorText>{t("deployments.loadFailed")}</ErrorText>
        ) : recent.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("deployments.empty")}</p>
        ) : (
          <ul className="grid gap-2">
            {recent.map((deployment) => (
              <li key={deployment.id} className="flex flex-wrap items-center gap-3 text-sm">
                <DeploymentStatusBadge status={deployment.status} />
                <Link to="/deployments/$deploymentId" params={{ deploymentId: deployment.id }} className="hover:underline">
                  {formatDateTime(deployment.queuedAt, i18n.resolvedLanguage)}
                </Link>
                {deployment.commit && <span className="text-muted-foreground font-mono">{shortCommit(deployment.commit)}</span>}
              </li>
            ))}
          </ul>
        )}
        <Link
          to="/stacks/$stackId/deployments"
          params={{ stackId: stack.id }}
          className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm"
        >
          {t("stack.recent.all")}
          <ArrowRightIcon className="size-4" />
        </Link>
      </CardContent>
    </Card>
  );
}

function StackLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();

  return <ErrorText>{hasStatus(error, 404) ? t("stack.notFound") : t("stack.loadFailed")}</ErrorText>;
}
