import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import {
  useCreateConnectionWebhookMutation,
  useDeleteGitConnectionMutation,
  useRemoveConnectionWebhookMutation,
  useRemoveGitConnectionTokenMutation,
  useSetGitConnectionTokenMutation,
  useUpdateGitConnectionMutation,
} from "@/api/git-connections";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, PencilIcon } from "@/components/icons";
import { WebhookSection } from "@/components/stacks/webhook-section";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import {
  type AccessTokenForm,
  type ConnectionDetailsForm,
  type GitConnection,
  accessTokenSchema,
  connectionDetailsOf,
  connectionDetailsSchema,
  connectionWebhookUrl,
  toAccessTokenRequest,
  toConnectionDetailsRequest,
} from "@/features/git-connections/model";
import { describeError } from "@/features/stacks/model";
import { AccessTokenFields } from "./access-token-fields";
import { ConnectionFields } from "./connection-fields";

const emptyToken: AccessTokenForm = { userName: "", token: "" };

/**
 * "Edit connection": name, prefix and environments; separately the token, the organization-wide webhook and
 * deleting the connection. Administrators only.
 */
export function EditConnectionDialog({ connection }: { connection: GitConnection }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [tokenSaved, setTokenSaved] = useState(false);
  const update = useUpdateGitConnectionMutation(connection.id);
  const setToken = useSetGitConnectionTokenMutation(connection.id);
  const removeToken = useRemoveGitConnectionTokenMutation(connection.id);
  const createWebhook = useCreateConnectionWebhookMutation(connection.id);
  const removeWebhook = useRemoveConnectionWebhookMutation(connection.id);
  const remove = useDeleteGitConnectionMutation(connection.id);

  const details = useForm<ConnectionDetailsForm>({
    resolver: zodResolver(connectionDetailsSchema),
    defaultValues: connectionDetailsOf(connection),
  });
  const token = useForm<AccessTokenForm>({ resolver: zodResolver(accessTokenSchema), defaultValues: emptyToken });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (next) {
      details.reset(connectionDetailsOf(connection));
    } else {
      token.reset(emptyToken);
      for (const mutation of [update, setToken, removeToken, createWebhook, removeWebhook, remove]) {
        mutation.reset();
      }
      setTokenSaved(false);
    }
  };

  const onSaveDetails = details.handleSubmit((form) =>
    update.mutate(toConnectionDetailsRequest(form), { onSuccess: () => onOpenChange(false) }),
  );

  const onSaveToken = token.handleSubmit((form) => {
    setTokenSaved(false);
    removeToken.reset();
    setToken.mutate(toAccessTokenRequest(form), {
      onSuccess: () => {
        setTokenSaved(true);
        // The form and the mutation's variables hold the token: drop both.
        token.reset(emptyToken);
        setToken.reset();
      },
    });
  });

  const onRemoveToken = () => {
    setTokenSaved(false);
    setToken.reset();
    removeToken.mutate();
  };

  const onDelete = () => remove.mutate(undefined, { onSuccess: () => setOpen(false) });

  const updateError = update.error ? describeError(update.error) : undefined;
  const tokenError = setToken.error ? describeError(setToken.error) : undefined;
  const removeTokenError = removeToken.error ? describeError(removeToken.error) : undefined;
  const deleteError = remove.error ? describeError(remove.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button variant="outline" size="sm" aria-label={t("gitConnections.edit.open", { name: connection.name })}>
          <PencilIcon data-icon="inline-start" />
          {t("gitConnections.edit.button")}
        </Button>
      </DialogTrigger>
      <DialogContent className="max-h-[90dvh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{t("gitConnections.edit.title", { name: connection.name })}</DialogTitle>
          <DialogDescription>{t("gitConnections.edit.description")}</DialogDescription>
        </DialogHeader>

        <form className="grid gap-4" noValidate onSubmit={onSaveDetails}>
          <ConnectionFields idPrefix="connection-edit" form={details} />
          {updateError && (
            <ErrorText>
              {t(updateError.key)} {updateError.detail}
            </ErrorText>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("stacks.cancel")}
            </Button>
            <Button type="submit" disabled={update.isPending}>
              {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("gitConnections.edit.save")}
            </Button>
          </DialogFooter>
        </form>

        <section aria-labelledby="connection-token-title" className="grid gap-4 border-t pt-4">
          <div>
            <h3 id="connection-token-title" className="font-heading font-semibold">
              {t("gitConnections.token.title")}
            </h3>
            <p className="text-muted-foreground mt-1 text-sm">
              {connection.hasAccessToken
                ? t("gitConnections.token.current", {
                    userName: connection.accessUserName ?? t("gitConnections.token.defaultUserName"),
                  })
                : t("gitConnections.token.none")}
            </p>
          </div>
          <form className="grid gap-4" noValidate onSubmit={onSaveToken}>
            <AccessTokenFields
              idPrefix="connection-edit"
              userName={token.register("userName")}
              token={token.register("token")}
              errors={{ userName: token.formState.errors.userName?.message, token: token.formState.errors.token?.message }}
            />
            <div className="flex flex-wrap gap-2">
              <Button type="submit" disabled={setToken.isPending}>
                {setToken.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {connection.hasAccessToken ? t("gitConnections.token.replace") : t("gitConnections.token.set")}
              </Button>
              {connection.hasAccessToken && (
                <ConfirmAction
                  title={t("gitConnections.token.removeTitle", { name: connection.name })}
                  description={t("gitConnections.token.removeDescription")}
                  confirmLabel={t("gitConnections.token.remove")}
                  disabled={removeToken.isPending}
                  onConfirm={onRemoveToken}
                >
                  {t("gitConnections.token.remove")}
                </ConfirmAction>
              )}
            </div>
          </form>
          <div aria-live="polite">
            {tokenError && (
              <ErrorText>
                {t("gitConnections.token.saveFailed")} {t(tokenError.key)} {tokenError.detail}
              </ErrorText>
            )}
            {removeTokenError && (
              <ErrorText>
                {t("gitConnections.token.removeFailed")} {t(removeTokenError.key)} {removeTokenError.detail}
              </ErrorText>
            )}
            {tokenSaved && (
              <p role="status" className="text-sm">
                {t("gitConnections.token.saved")}
              </p>
            )}
          </div>
        </section>

        <WebhookSection
          name={connection.name}
          hasWebhook={connection.hasWebhook}
          url={connectionWebhookUrl(window.location.origin, connection.id)}
          description={t("gitConnections.webhook.description")}
          create={createWebhook}
          remove={removeWebhook}
        />

        <section aria-labelledby="connection-delete-title" className="grid gap-3 border-t pt-4">
          <div>
            <h3 id="connection-delete-title" className="font-heading font-semibold">
              {t("gitConnections.delete.title")}
            </h3>
            <p className="text-muted-foreground mt-1 text-sm">
              {Number(connection.repositoryCount) > 0
                ? t("gitConnections.delete.inUse", { count: connection.repositoryCount })
                : t("gitConnections.delete.description")}
            </p>
          </div>
          <div>
            <ConfirmAction
              title={t("gitConnections.delete.confirmTitle", { name: connection.name })}
              description={t("gitConnections.delete.confirmDescription")}
              confirmLabel={t("gitConnections.delete.button")}
              disabled={remove.isPending || Number(connection.repositoryCount) > 0}
              onConfirm={onDelete}
            >
              {t("gitConnections.delete.button")}
            </ConfirmAction>
          </div>
          {deleteError && (
            <ErrorText>
              {t("gitConnections.delete.failed")} {t(deleteError.key)} {deleteError.detail}
            </ErrorText>
          )}
        </section>
      </DialogContent>
    </Dialog>
  );
}
