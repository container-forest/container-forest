import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { Link, createFileRoute } from "@tanstack/react-router";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import {
  useDeleteRegistryCredentialMutation,
  useIssueEnrollmentTokenMutation,
  useRevokeAgentMutation,
  useSetRegistryCredentialMutation,
} from "@/api/environments";
import { environmentsQuery, registryCredentialsQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { EditEnvironmentDialog } from "@/components/environments/environment-dialogs";
import { EnrollmentInstructions } from "@/components/environments/enrollment-instructions";
import { ErrorText, FieldError } from "@/components/form-messages";
import { ArrowLeftIcon, KeyIcon, Loader2Icon, LockIcon, Trash2Icon, UnlinkIcon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  type Environment,
  type RegistryCredentialForm,
  STATUS_REFRESH_MS,
  agentStatusOf,
  describeError,
  registryCredentialSchema,
  statusVariant,
  tokenStateOf,
} from "@/features/environments/model";
import { formatDateTime } from "@/lib/format";

export const Route = createFileRoute("/_authed/environments/$environmentId")({
  loader: ({ context }) => context.queryClient.ensureQueryData(environmentsQuery),
  component: EnvironmentScreen,
});

/** One environment: its agent's status and, for administrators, enrolling an agent and registry logins. */
function EnvironmentScreen() {
  const { t } = useTranslation();
  const { me } = Route.useRouteContext();
  const { environmentId } = Route.useParams();
  const environments = useQuery({ ...environmentsQuery, refetchInterval: STATUS_REFRESH_MS });
  const environment = environments.data?.find((e) => e.id === environmentId);

  return (
    <div className="grid gap-6">
      <div>
        <Button asChild variant="ghost" size="sm" className="-ml-2">
          <Link to="/environments">
            <ArrowLeftIcon data-icon="inline-start" />
            {t("environment.back")}
          </Link>
        </Button>
      </div>

      {environment === undefined ? (
        // A missing environment and one the user may not see look the same, as in the aggregator.
        <ErrorText>{t("environments.errors.notFound")}</ErrorText>
      ) : (
        <>
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <h1 className="font-heading flex items-center gap-2 text-2xl font-semibold">
                {environment.name}
                {environment.isPrivate && <LockIcon className="text-muted-foreground size-5" aria-label={t("environments.private")} />}
              </h1>
              <p className="text-muted-foreground mt-1">{t("environment.description")}</p>
            </div>
            {me.isAdmin && <EditEnvironmentDialog environment={environment} />}
          </div>

          <AgentStatusCard environment={environment} />
          {me.isAdmin && (
            <>
              {/* A new key per environment drops a token shown for another one. */}
              <EnrollmentSection key={environment.id} environment={environment} />
              <RegistryCredentialsSection environmentId={environment.id} />
            </>
          )}
        </>
      )}
    </div>
  );
}

function AgentStatusCard({ environment }: { environment: Environment }) {
  const { t, i18n } = useTranslation();
  const status = agentStatusOf(environment);

  return (
    <section aria-labelledby="agent-status" className="grid gap-3 rounded-lg border p-4">
      <div className="flex items-center gap-3">
        <h2 id="agent-status" className="font-heading text-lg font-semibold">
          {t("environment.agent")}
        </h2>
        <Badge variant={statusVariant(status)}>{t(`environments.status.${status}`)}</Badge>
      </div>
      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[auto_1fr]">
        <dt className="text-muted-foreground">{t("environments.columns.host")}</dt>
        <dd className="font-mono">{environment.agentHostname ?? "—"}</dd>
        <dt className="text-muted-foreground">{t("environments.columns.version")}</dt>
        <dd>{environment.agentVersion ?? "—"}</dd>
        <dt className="text-muted-foreground">{t("environments.columns.lastSeen")}</dt>
        <dd>
          {status === "online"
            ? t("environments.connectedNow")
            : environment.lastSeenAt
              ? formatDateTime(environment.lastSeenAt, i18n.resolvedLanguage)
              : "—"}
        </dd>
      </dl>
      {status === "offline" && <p className="text-muted-foreground text-sm">{t("environment.offlineHint")}</p>}
    </section>
  );
}

/** The issued token: kept only in this section's state, never in the query cache, the URL or a log. */
interface IssuedToken {
  token: string;
  expiresAt: string;
}

function EnrollmentSection({ environment }: { environment: Environment }) {
  const { t, i18n } = useTranslation();
  const issue = useIssueEnrollmentTokenMutation(environment.id);
  const revoke = useRevokeAgentMutation(environment.id);
  const [issued, setIssued] = useState<IssuedToken>();
  const tokenState = tokenStateOf(environment, new Date());

  const issueToken = () =>
    issue.mutate(undefined, {
      onSuccess: (answer) => {
        setIssued({ token: answer.enrollmentToken, expiresAt: answer.expiresAt });
        // The answer holds the token; the mutation should not keep it.
        issue.reset();
      },
    });

  const issueError = issue.error ? describeError(issue.error) : undefined;
  const revokeError = revoke.error ? describeError(revoke.error) : undefined;
  // A new token cancels the one shown or waiting, and enrolling with it replaces the current agent: ask first then.
  const replaces = issued !== undefined || tokenState === "active" || environment.isEnrolled;

  return (
    <section aria-labelledby="enrollment" className="grid gap-4 rounded-lg border p-4">
      <div>
        <h2 id="enrollment" className="font-heading text-lg font-semibold">
          {t("environment.enrollment.title")}
        </h2>
        <p className="text-muted-foreground mt-1 text-sm">
          {environment.isEnrolled ? t("environment.enrollment.enrolledHint") : t("environment.enrollment.hint")}
        </p>
        {tokenState !== "none" && environment.enrollmentTokenExpiresAt && !issued && (
          <p className="mt-1 text-sm">
            {t(`environment.enrollment.waiting.${tokenState}`, {
              time: formatDateTime(environment.enrollmentTokenExpiresAt, i18n.resolvedLanguage),
            })}
          </p>
        )}
      </div>

      <div className="flex flex-wrap gap-2">
        {replaces ? (
          <ConfirmAction
            title={t("environment.enrollment.replaceTitle")}
            description={t("environment.enrollment.replaceDescription")}
            confirmLabel={t("environment.enrollment.issue")}
            disabled={issue.isPending}
            onConfirm={issueToken}
          >
            {issue.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <KeyIcon data-icon="inline-start" />}
            {t("environment.enrollment.issue")}
          </ConfirmAction>
        ) : (
          <Button variant="outline" disabled={issue.isPending} onClick={issueToken}>
            {issue.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <KeyIcon data-icon="inline-start" />}
            {t("environment.enrollment.issue")}
          </Button>
        )}

        {environment.isEnrolled && (
          <ConfirmAction
            title={t("environment.revoke.title", { name: environment.name })}
            description={t("environment.revoke.description")}
            confirmLabel={t("environment.revoke.confirm")}
            disabled={revoke.isPending}
            onConfirm={() => revoke.mutate()}
          >
            {revoke.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <UnlinkIcon data-icon="inline-start" />}
            {t("environment.revoke.button")}
          </ConfirmAction>
        )}
      </div>

      <div aria-live="polite">
        {issueError && (
          <ErrorText>
            {t("environment.enrollment.failed")} {t(issueError.key)} {issueError.detail}
          </ErrorText>
        )}
        {revokeError && (
          <ErrorText>
            {t("environment.revoke.failed")} {t(revokeError.key)} {revokeError.detail}
          </ErrorText>
        )}
        {revoke.isSuccess && <p role="status" className="text-sm">{t("environment.revoke.done")}</p>}
      </div>

      {issued && <EnrollmentInstructions environmentName={environment.name} token={issued.token} expiresAt={issued.expiresAt} />}
    </section>
  );
}

const emptyCredential: RegistryCredentialForm = { server: "", username: "", password: "" };

function RegistryCredentialsSection({ environmentId }: { environmentId: string }) {
  const { t, i18n } = useTranslation();
  const credentials = useQuery(registryCredentialsQuery(environmentId));
  const save = useSetRegistryCredentialMutation(environmentId);
  const remove = useDeleteRegistryCredentialMutation(environmentId);
  const [saved, setSaved] = useState<string>();
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<RegistryCredentialForm>({ resolver: zodResolver(registryCredentialSchema), defaultValues: emptyCredential });

  const onSubmit = handleSubmit((form) => {
    setSaved(undefined);
    save.mutate(form, {
      onSuccess: (credential) => {
        setSaved(credential.server);
        // The form and the mutation's variables hold the password: drop both.
        reset(emptyCredential);
        save.reset();
      },
    });
  });

  const saveError = save.error ? describeError(save.error) : undefined;
  const removeError = remove.error ? describeError(remove.error) : undefined;
  const loadError = credentials.error ? describeError(credentials.error) : undefined;
  const list = credentials.data ?? [];

  return (
    <section aria-labelledby="registry" className="grid gap-4 rounded-lg border p-4">
      <div>
        <h2 id="registry" className="font-heading text-lg font-semibold">
          {t("environments.registry.title")}
        </h2>
        <p className="text-muted-foreground mt-1 text-sm">{t("environments.registry.description")}</p>
      </div>

      {credentials.isPending ? (
        <p className="text-muted-foreground flex items-center gap-2 text-sm">
          <Loader2Icon className="animate-spin" />
          {t("environments.registry.loading")}
        </p>
      ) : loadError ? (
        <ErrorText>
          {t("environments.registry.loadFailed")} {t(loadError.key)} {loadError.detail}
        </ErrorText>
      ) : list.length === 0 ? (
        <p className="text-muted-foreground text-sm">{t("environments.registry.empty")}</p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("environments.registry.server")}</TableHead>
              <TableHead>{t("environments.registry.username")}</TableHead>
              <TableHead>{t("environments.registry.updated")}</TableHead>
              <TableHead className="text-right">{t("environments.columns.actions")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.map((credential) => (
              <TableRow key={credential.id}>
                <TableCell className="font-mono text-xs">{credential.server}</TableCell>
                <TableCell>{credential.username}</TableCell>
                <TableCell className="text-sm whitespace-nowrap">{formatDateTime(credential.updatedAt, i18n.resolvedLanguage)}</TableCell>
                <TableCell className="text-right">
                  <ConfirmAction
                    variant="ghost"
                    size="icon"
                    title={t("environments.registry.deleteTitle", { server: credential.server })}
                    description={t("environments.registry.deleteDescription")}
                    confirmLabel={t("environments.registry.delete")}
                    disabled={remove.isPending}
                    onConfirm={() => remove.mutate(credential.id)}
                  >
                    <Trash2Icon />
                    <span className="sr-only">
                      {t("environments.registry.delete")}: {credential.server}
                    </span>
                  </ConfirmAction>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {removeError && (
        <ErrorText>
          {t("environments.registry.deleteFailed")} {t(removeError.key)} {removeError.detail}
        </ErrorText>
      )}

      <form className="grid gap-3 sm:grid-cols-[1fr_1fr_1fr_auto] sm:items-start" noValidate onSubmit={onSubmit}>
        <div className="grid gap-1.5">
          <Label htmlFor="registry-server">{t("environments.registry.server")}</Label>
          <Input
            id="registry-server"
            autoComplete="off"
            placeholder="ghcr.io"
            aria-invalid={errors.server ? true : undefined}
            aria-describedby={errors.server ? "registry-server-error" : undefined}
            {...register("server")}
          />
          <FieldError id="registry-server-error" message={errors.server?.message} />
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="registry-username">{t("environments.registry.username")}</Label>
          <Input
            id="registry-username"
            autoComplete="off"
            aria-invalid={errors.username ? true : undefined}
            aria-describedby={errors.username ? "registry-username-error" : undefined}
            {...register("username")}
          />
          <FieldError id="registry-username-error" message={errors.username?.message} />
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="registry-password">{t("environments.registry.password")}</Label>
          <Input
            id="registry-password"
            type="password"
            // Not the user's own password: browsers should neither offer nor save it.
            autoComplete="new-password"
            aria-invalid={errors.password ? true : undefined}
            aria-describedby={errors.password ? "registry-password-error" : undefined}
            {...register("password")}
          />
          <FieldError id="registry-password-error" message={errors.password?.message} />
        </div>
        <Button type="submit" className="sm:mt-6" disabled={save.isPending}>
          {save.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
          {t("environments.registry.save")}
        </Button>
      </form>
      <p className="text-muted-foreground text-sm">{t("environments.registry.replaceHint")}</p>
      <div aria-live="polite">
        {saveError && (
          <ErrorText>
            {t("environments.registry.saveFailed")} {t(saveError.key)} {saveError.detail}
          </ErrorText>
        )}
        {saved && <p role="status" className="text-sm">{t("environments.registry.saved", { server: saved })}</p>}
      </div>
    </section>
  );
}
