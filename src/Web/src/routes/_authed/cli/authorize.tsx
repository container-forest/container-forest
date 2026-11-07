import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { Link, createFileRoute } from "@tanstack/react-router";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import { useApproveCliAuthorizationMutation, useDenyCliAuthorizationMutation } from "@/api/cli-login";
import { cliAuthorizationQuery } from "@/api/queries";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, TerminalIcon } from "@/components/icons";
import { TokenOptionsFields } from "@/components/tokens/token-options-fields";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  type CliAuthorization,
  cliAuthorizeSearchSchema,
  defaultTokenName,
  formatUserCode,
  loopbackUrl,
} from "@/features/cli-login/model";
import { describeError } from "@/features/stacks/model";
import { type CreateTokenForm, createTokenSchema, emptyCreateTokenForm, toCreateTokenRequest } from "@/features/tokens/model";
import { formatDateTime } from "@/lib/format";

export const Route = createFileRoute("/_authed/cli/authorize")({
  validateSearch: cliAuthorizeSearchSchema,
  component: CliAuthorizeScreen,
});

type Decision = "approved" | "denied";

/** A CLI asks for a token of the signed-in user: what asks, the code to compare with the terminal, the token's limits. */
function CliAuthorizeScreen() {
  const { t } = useTranslation();
  const { code, port } = Route.useSearch();
  const [decision, setDecision] = useState<Decision>();
  // Decided requests answer 404, so the query stops once there is a decision.
  const request = useQuery({ ...cliAuthorizationQuery(code), enabled: code !== "" && decision === undefined });

  return (
    <div className="mx-auto grid w-full max-w-2xl gap-6">
      <div>
        <h1 className="font-heading flex items-center gap-2 text-2xl font-semibold">
          <TerminalIcon className="size-7" />
          {t("cliLogin.title")}
        </h1>
        <p className="text-muted-foreground mt-1">{t("cliLogin.description")}</p>
      </div>

      {decision !== undefined ? (
        <DecisionDone decision={decision} notified={port !== undefined} />
      ) : code === "" || (request.isError && hasStatus(request.error, 404)) ? (
        <NotFound />
      ) : request.isPending ? (
        <p className="text-muted-foreground flex items-center gap-2">
          <Loader2Icon className="animate-spin" />
          {t("cliLogin.loading")}
        </p>
      ) : request.isError ? (
        <ErrorText>
          {t("cliLogin.loadFailed")} {t(describeError(request.error).key)}
        </ErrorText>
      ) : (
        <ApproveForm
          request={request.data}
          code={code}
          onDecided={(made) => {
            setDecision(made);
            // Wakes the CLI up at once; it hears nothing secret, only that it is time to poll.
            if (port !== undefined) {
              window.location.assign(loopbackUrl(port, made));
            }
          }}
        />
      )}
    </div>
  );
}

function ApproveForm({
  request,
  code,
  onDecided,
}: {
  request: CliAuthorization;
  code: string;
  onDecided: (decision: Decision) => void;
}) {
  const { t, i18n } = useTranslation();
  const approve = useApproveCliAuthorizationMutation(code);
  const deny = useDenyCliAuthorizationMutation(code);
  const form = useForm<CreateTokenForm>({
    resolver: zodResolver(createTokenSchema),
    // The CLI may ask for the right to decrypt secrets (forest login --secrets); the box is ticked, the person decides.
    defaultValues: { ...emptyCreateTokenForm, name: defaultTokenName(request.hostName), secrets: request.secretsRequested },
  });
  const errors = form.formState.errors;
  const busy = approve.isPending || deny.isPending;

  const onSubmit = form.handleSubmit((values) =>
    approve.mutate(toCreateTokenRequest(values, new Date()), { onSuccess: () => onDecided("approved") }),
  );
  const error = approve.error ?? deny.error;
  const described = error ? describeError(error) : undefined;

  return (
    <>
      <Card>
        <CardHeader>
          <CardTitle>{t("cliLogin.request.title", { host: request.hostName })}</CardTitle>
          <CardDescription>{t("cliLogin.request.compare")}</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          <code className="bg-muted w-fit rounded-md px-4 py-2 font-mono text-2xl tracking-widest">{formatUserCode(request.userCode)}</code>
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
            <dt className="text-muted-foreground">{t("cliLogin.request.client")}</dt>
            <dd className="break-all">{request.clientName}</dd>
            <dt className="text-muted-foreground">{t("cliLogin.request.host")}</dt>
            <dd className="break-all">{request.hostName}</dd>
            <dt className="text-muted-foreground">{t("cliLogin.request.address")}</dt>
            <dd className="font-mono break-all">{request.requestedFrom ?? t("cliLogin.request.unknownAddress")}</dd>
            <dt className="text-muted-foreground">{t("cliLogin.request.at")}</dt>
            <dd>{formatDateTime(request.createdAt, i18n.resolvedLanguage)}</dd>
          </dl>
          <p className="border-destructive/40 bg-destructive/5 rounded-md border p-3 text-sm" role="note">
            {t("cliLogin.request.warning")}
          </p>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("cliLogin.token.title")}</CardTitle>
          <CardDescription>{t("cliLogin.token.description")}</CardDescription>
          {request.secretsRequested && <CardDescription>{t("cliLogin.token.secretsRequested")}</CardDescription>}
        </CardHeader>
        <CardContent>
          <form className="grid gap-4" noValidate onSubmit={onSubmit}>
            <div className="grid gap-1.5">
              <Label htmlFor="cli-token-name">{t("tokens.form.name")}</Label>
              <Input
                id="cli-token-name"
                autoComplete="off"
                aria-invalid={errors.name ? true : undefined}
                aria-describedby={errors.name ? "cli-token-name-error" : undefined}
                {...form.register("name")}
              />
              <FieldError id="cli-token-name-error" message={errors.name?.message} />
            </div>
            <TokenOptionsFields idPrefix="cli-token" form={form} />

            {described && (
              <ErrorText>
                {t("cliLogin.failed")} {t(described.key)} {described.detail}
              </ErrorText>
            )}
            <div className="flex flex-wrap gap-2">
              <Button type="submit" disabled={busy}>
                {approve.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("cliLogin.approve")}
              </Button>
              <Button
                type="button"
                variant="outline"
                disabled={busy}
                onClick={() => deny.mutate(undefined, { onSuccess: () => onDecided("denied") })}
              >
                {deny.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("cliLogin.deny")}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </>
  );
}

function DecisionDone({ decision, notified }: { decision: Decision; notified: boolean }) {
  const { t } = useTranslation();

  return (
    <Card role="status">
      <CardHeader>
        <CardTitle>{decision === "approved" ? t("cliLogin.done.approved") : t("cliLogin.done.denied")}</CardTitle>
        <CardDescription>{notified ? t("cliLogin.done.notified") : t("cliLogin.done.back")}</CardDescription>
      </CardHeader>
      {decision === "approved" && (
        <CardContent>
          <Link to="/tokens" className="text-sm underline underline-offset-4">
            {t("cliLogin.done.tokens")}
          </Link>
        </CardContent>
      )}
    </Card>
  );
}

function NotFound() {
  const { t } = useTranslation();

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("cliLogin.notFound.title")}</CardTitle>
        <CardDescription>{t("cliLogin.notFound.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <Link to="/cli/device" className="text-sm underline underline-offset-4">
          {t("cliLogin.notFound.enterCode")}
        </Link>
      </CardContent>
    </Card>
  );
}
