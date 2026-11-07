import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, createFileRoute, redirect } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCreateAgeKeyMutation, useDeleteAgeKeyMutation } from "@/api/keys";
import { ageKeysQuery, environmentsQuery, stacksQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, PlusIcon, Trash2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { Environment } from "@/features/containers/model";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { type AgeKey, currentKey, isCurrent, keysSearchSchema, sopsCommands, sopsConfig } from "@/features/keys/model";
import { describeError } from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";

export const Route = createFileRoute("/_authed/keys")({
  validateSearch: keysSearchSchema,
  beforeLoad: async ({ context, search }) => {
    const environments = await context.queryClient.ensureQueryData(environmentsQuery);
    // No environment in the URL, or one the user cannot see (any more): open the first one.
    const first = environments[0];
    if (first !== undefined && !environments.some((e) => e.id === search.environment)) {
      throw redirect({ to: "/keys", search: { environment: first.id }, replace: true });
    }
  },
  component: KeysScreen,
  errorComponent: KeysLoadError,
});

/** The age keys SOPS files of an environment are encrypted for, and how to use them in a repository. */
function KeysScreen() {
  const { t } = useTranslation();
  const search = Route.useSearch();
  const { data: environments } = useSuspenseQuery(environmentsQuery);
  const environment = environments.find((e) => e.id === search.environment);

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="max-w-2xl">
          <h1 className="font-heading flex flex-wrap items-baseline gap-x-3 text-2xl font-semibold">
            {t("keys.title")}
            {environment && <span className="text-muted-foreground text-lg font-medium">{environment.name}</span>}
          </h1>
          <p className="text-muted-foreground mt-1">{t("keys.description")}</p>
        </div>
      </div>

      {environment === undefined ? (
        <p className="text-muted-foreground">{t("keys.noEnvironments")}</p>
      ) : (
        <EnvironmentKeys key={environment.id} environment={environment} />
      )}
    </div>
  );
}

function EnvironmentKeys({ environment }: { environment: Environment }) {
  const { t, i18n } = useTranslation();
  const keys = useQuery(ageKeysQuery(environment.id));
  const create = useCreateAgeKeyMutation(environment.id);
  const remove = useDeleteAgeKeyMutation(environment.id);
  const stacks = useQuery(stacksQuery(environment.id));
  const [stackForKey, setStackForKey] = useState("");
  const stackName = (stackId: string | null | undefined) =>
    stackId ? (stacks.data?.find((s) => s.id === stackId)?.name ?? stackId) : t("keys.wholeEnvironment");

  if (keys.isPending) {
    return (
      <p className="text-muted-foreground flex items-center gap-2">
        <Loader2Icon className="animate-spin" />
        {t("keys.loading")}
      </p>
    );
  }

  if (keys.isError) {
    const { key, detail } = describeError(keys.error);
    return (
      <ErrorText>
        {t("keys.loadFailed")} {t(key)} {detail}
      </ErrorText>
    );
  }

  const current = currentKey(keys.data);
  const createError = create.error ? describeError(create.error) : undefined;
  const removeError = remove.error ? describeError(remove.error) : undefined;
  const onCreate = () => {
    remove.reset();
    create.mutate(null);
  };
  const onCreateForStack = () => {
    remove.reset();
    create.mutate(stackForKey, { onSuccess: () => setStackForKey("") });
  };
  const onDelete = (key: AgeKey) => {
    create.reset();
    remove.mutate(key.id);
  };

  return (
    <div className="grid gap-6">
      <section aria-labelledby="keys-list" className="grid gap-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="keys-list" className="font-heading text-lg font-semibold">
            {t("keys.listTitle", { name: environment.name })}
          </h2>
          {/* Only a convenience: the aggregator refuses anyone who does not administer the environment anyway. */}
          {environment.canAdminister &&
            (current === undefined ? (
              <Button onClick={onCreate} disabled={create.isPending}>
                {create.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <PlusIcon data-icon="inline-start" />}
                {t("keys.create")}
              </Button>
            ) : (
              <ConfirmAction
                variant="default"
                title={t("keys.rotateTitle", { name: environment.name })}
                description={t("keys.rotateDescription")}
                confirmLabel={t("keys.create")}
                disabled={create.isPending}
                onConfirm={onCreate}
              >
                {create.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <PlusIcon data-icon="inline-start" />}
                {t("keys.create")}
              </ConfirmAction>
            ))}
        </div>

        {keys.data.length === 0 ? (
          <p className="text-muted-foreground">{t("keys.empty")}</p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("keys.columns.recipient")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("keys.columns.scope")}</TableHead>
                <TableHead>{t("keys.columns.createdAt")}</TableHead>
                <TableHead className="sr-only">{t("keys.columns.actions")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {keys.data.map((key) => (
                <TableRow key={key.id}>
                  <TableCell>
                    <div className="flex flex-wrap items-center gap-2">
                      <code className="font-mono text-xs break-all">{key.recipient}</code>
                      {isCurrent(keys.data, key) ? (
                        <Badge>{t("keys.current")}</Badge>
                      ) : (
                        <Badge variant="outline">{t("keys.previous")}</Badge>
                      )}
                    </div>
                  </TableCell>
                  <TableCell className="text-muted-foreground hidden text-sm sm:table-cell">{stackName(key.stackId)}</TableCell>
                  <TableCell className="text-sm whitespace-nowrap">{formatDateTime(key.createdAt, i18n.resolvedLanguage)}</TableCell>
                  <TableCell>
                    <div className="flex justify-end gap-2">
                      <CopyButton text={key.recipient} label={t("keys.copy")} />
                      {environment.canAdminister && (
                        <ConfirmAction
                          variant="outline"
                          size="sm"
                          title={t("keys.deleteTitle")}
                          description={t("keys.deleteDescription", { recipient: key.recipient })}
                          confirmLabel={t("keys.delete")}
                          disabled={remove.isPending}
                          onConfirm={() => onDelete(key)}
                        >
                          <Trash2Icon data-icon="inline-start" />
                          {t("keys.delete")}
                        </ConfirmAction>
                      )}
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {environment.canAdminister && (stacks.data?.length ?? 0) > 0 && (
          <div className="grid gap-2 border-t pt-4">
            <h3 className="font-medium">{t("keys.stackKey.title")}</h3>
            <p className="text-muted-foreground text-sm">{t("keys.stackKey.description")}</p>
            <div className="flex flex-wrap items-center gap-3">
              <Select value={stackForKey} onValueChange={(value) => value !== "" && setStackForKey(value)}>
                <SelectTrigger className="w-56" aria-label={t("keys.stackKey.stack")}>
                  <SelectValue placeholder={t("keys.stackKey.stack")} />
                </SelectTrigger>
                <SelectContent>
                  {stacks.data?.map((stack) => (
                    <SelectItem key={stack.id} value={stack.id}>
                      {stack.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Button variant="outline" onClick={onCreateForStack} disabled={stackForKey === "" || create.isPending}>
                <PlusIcon data-icon="inline-start" />
                {t("keys.stackKey.create")}
              </Button>
            </div>
          </div>
        )}
        <div aria-live="polite">
          {createError && (
            <ErrorText>
              {t("keys.createFailed")} {t(createError.key)} {createError.detail}
            </ErrorText>
          )}
          {removeError && (
            <ErrorText>
              {t("keys.deleteFailed")} {t(removeError.key)} {removeError.detail}
            </ErrorText>
          )}
        </div>
      </section>

      {current && <HowTo environment={environment} recipient={current.recipient} />}
    </div>
  );
}

function HowTo({ environment, recipient }: { environment: Environment; recipient: string }) {
  const { t } = useTranslation();
  const config = sopsConfig(environment.name, recipient);

  return (
    <section aria-labelledby="keys-howto" className="grid gap-4 rounded-lg border p-4">
      <div>
        <h2 id="keys-howto" className="font-heading text-lg font-semibold">
          {t("keys.howTo.title")}
        </h2>
        <p className="text-muted-foreground mt-1 text-sm">{t("keys.howTo.description")}</p>
      </div>
      <ol className="grid list-decimal gap-4 pl-5 text-sm">
        <li className="grid gap-2">
          <span>{t("keys.howTo.config")}</span>
          <pre className="bg-muted overflow-x-auto rounded-md p-3 font-mono text-xs">{config}</pre>
          <div>
            <CopyButton text={config} label={t("keys.howTo.copyConfig")} />
          </div>
        </li>
        <li className="grid gap-2">
          <span>{t("keys.howTo.encrypt")}</span>
          <pre className="bg-muted overflow-x-auto rounded-md p-3 font-mono text-xs">{sopsCommands.encrypt}</pre>
        </li>
        <li>{t("keys.howTo.deploy")}</li>
        <li className="grid gap-2">
          <span>{t("keys.howTo.rotate")}</span>
          <pre className="bg-muted overflow-x-auto rounded-md p-3 font-mono text-xs">{sopsCommands.updateKeys}</pre>
        </li>
      </ol>
    </section>
  );
}

function KeysLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();
  const { key, detail } = describeError(error);

  return (
    <ErrorText>
      {t("keys.loadFailed")} {t(key)} {detail}
    </ErrorText>
  );
}
