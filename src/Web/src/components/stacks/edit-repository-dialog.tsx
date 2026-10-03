import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import {
  useRemoveRepositoryAccessTokenMutation,
  useSetRepositoryAccessTokenMutation,
  useUpdateRepositoryMutation,
} from "@/api/stacks";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PencilIcon } from "@/components/icons";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  type AccessTokenForm,
  type EditRepositoryForm,
  type Repository,
  accessTokenSchema,
  describeError,
  editRepositorySchema,
  movesTokenToAnotherHost,
  toAccessTokenRequest,
} from "@/features/stacks/model";
import { AccessTokenFields } from "./access-token-fields";
import { WebhookSection } from "./webhook-section";

const emptyToken: AccessTokenForm = { userName: "", token: "" };

/** "Edit repository": name and URL, and separately the access token; administrators only. */
export function EditRepositoryDialog({ repository }: { repository: Repository }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [tokenSaved, setTokenSaved] = useState(false);
  const update = useUpdateRepositoryMutation(repository.id);
  const setToken = useSetRepositoryAccessTokenMutation(repository.id);
  const removeToken = useRemoveRepositoryAccessTokenMutation(repository.id);

  const details = useForm<EditRepositoryForm>({
    resolver: zodResolver(editRepositorySchema),
    defaultValues: { name: repository.name, url: repository.url },
  });
  const url = useWatch({ control: details.control, name: "url" });
  const blockedByToken = movesTokenToAnotherHost(repository, url);

  const token = useForm<AccessTokenForm>({ resolver: zodResolver(accessTokenSchema), defaultValues: emptyToken });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (next) {
      details.reset({ name: repository.name, url: repository.url });
    } else {
      token.reset(emptyToken);
      update.reset();
      setToken.reset();
      removeToken.reset();
      setTokenSaved(false);
    }
  };

  const onSaveDetails = details.handleSubmit((form) => {
    if (!blockedByToken) {
      update.mutate(form, { onSuccess: () => onOpenChange(false) });
    }
  });

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

  const updateError = update.error && !hasStatus(update.error, 409) ? describeError(update.error) : undefined;
  const tokenError = setToken.error ? describeError(setToken.error) : undefined;
  const removeError = removeToken.error ? describeError(removeToken.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button variant="outline" size="sm" aria-label={t("repositories.edit.open", { name: repository.name })}>
          <PencilIcon data-icon="inline-start" />
          {t("repositories.edit.button")}
        </Button>
      </DialogTrigger>
      <DialogContent className="max-h-[90dvh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{t("repositories.edit.title", { name: repository.name })}</DialogTitle>
          <DialogDescription>{t("repositories.edit.description")}</DialogDescription>
        </DialogHeader>

        <form className="grid gap-4" noValidate onSubmit={onSaveDetails}>
          <div className="grid gap-1.5">
            <Label htmlFor="repository-edit-name">{t("repositories.form.name")}</Label>
            <Input
              id="repository-edit-name"
              autoComplete="off"
              aria-invalid={details.formState.errors.name ? true : undefined}
              aria-describedby={details.formState.errors.name ? "repository-edit-name-error" : undefined}
              {...details.register("name")}
            />
            <FieldError id="repository-edit-name-error" message={details.formState.errors.name?.message} />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="repository-edit-url">{t("repositories.form.url")}</Label>
            <Input
              id="repository-edit-url"
              type="url"
              autoComplete="off"
              aria-invalid={details.formState.errors.url || blockedByToken ? true : undefined}
              aria-describedby={
                details.formState.errors.url
                  ? "repository-edit-url-error"
                  : blockedByToken
                    ? "repository-edit-url-host"
                    : undefined
              }
              {...details.register("url")}
            />
            <FieldError id="repository-edit-url-error" message={details.formState.errors.url?.message} />
            {blockedByToken && !details.formState.errors.url && (
              <p id="repository-edit-url-host" className="text-destructive text-sm">
                {t("repositories.edit.otherHost")}
              </p>
            )}
          </div>
          {update.error && hasStatus(update.error, 409) && <ErrorText>{t("repositories.create.nameTaken")}</ErrorText>}
          {updateError && (
            <ErrorText>
              {t(updateError.key)} {updateError.detail}
            </ErrorText>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("stacks.cancel")}
            </Button>
            <Button type="submit" disabled={update.isPending || blockedByToken}>
              {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("repositories.edit.save")}
            </Button>
          </DialogFooter>
        </form>

        <section aria-labelledby="repository-token-title" className="grid gap-4 border-t pt-4">
          <div>
            <h3 id="repository-token-title" className="font-heading font-semibold">
              {t("repositories.token.title")}
            </h3>
            <p className="text-muted-foreground mt-1 text-sm">
              {repository.hasAccessToken
                ? t("repositories.token.current", {
                    userName: repository.accessUserName ?? t("repositories.token.defaultUserName"),
                  })
                : t("repositories.token.none")}
            </p>
          </div>
          <form className="grid gap-4" noValidate onSubmit={onSaveToken}>
            <AccessTokenFields
              idPrefix="repository-edit"
              userName={token.register("userName")}
              token={token.register("token")}
              errors={{
                userName: token.formState.errors.userName?.message,
                token: token.formState.errors.token?.message,
              }}
            />
            <div className="flex flex-wrap gap-2">
              <Button type="submit" disabled={setToken.isPending}>
                {setToken.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {repository.hasAccessToken ? t("repositories.token.replace") : t("repositories.token.set")}
              </Button>
              {repository.hasAccessToken && (
                <ConfirmAction
                  title={t("repositories.token.removeTitle", { name: repository.name })}
                  description={t("repositories.token.removeDescription")}
                  confirmLabel={t("repositories.token.remove")}
                  disabled={removeToken.isPending}
                  onConfirm={onRemoveToken}
                >
                  {t("repositories.token.remove")}
                </ConfirmAction>
              )}
            </div>
          </form>
          <div aria-live="polite">
            {tokenError && (
              <ErrorText>
                {t("repositories.token.saveFailed")} {t(tokenError.key)} {tokenError.detail}
              </ErrorText>
            )}
            {removeError && (
              <ErrorText>
                {t("repositories.token.removeFailed")} {t(removeError.key)} {removeError.detail}
              </ErrorText>
            )}
            {tokenSaved && (
              <p role="status" className="text-sm">
                {t("repositories.token.saved")}
              </p>
            )}
          </div>
        </section>

        <WebhookSection repository={repository} />
      </DialogContent>
    </Dialog>
  );
}
