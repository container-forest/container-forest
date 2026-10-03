import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useCreateWebhookSecretMutation, useRemoveWebhookSecretMutation, useUpdateRepositoryMutation } from "@/api/stacks";
import { ErrorText } from "@/components/form-messages";
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
import {
  type Repository,
  type RepositoryForm,
  describeError,
  repositoryFormOf,
  repositorySchema,
  toRepositoryRequest,
  webhookUrl,
} from "@/features/stacks/model";
import { RepositoryFields, useRepositoryFormBlocked } from "./repository-fields";
import { WebhookSection } from "./webhook-section";

/**
 * "Edit repository": name, connection and address, and the repository's own webhook. Shown when the user may manage
 * it (canManage): an administrator, or someone who may deploy every stack of the repository.
 */
export function EditRepositoryDialog({ repository }: { repository: Repository }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const update = useUpdateRepositoryMutation(repository.id);
  const createWebhook = useCreateWebhookSecretMutation(repository.id);
  const removeWebhook = useRemoveWebhookSecretMutation(repository.id);
  const form = useForm<RepositoryForm>({ resolver: zodResolver(repositorySchema), defaultValues: repositoryFormOf(repository) });
  const blocked = useRepositoryFormBlocked(form);

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (next) {
      form.reset(repositoryFormOf(repository));
    } else {
      update.reset();
      createWebhook.reset();
      removeWebhook.reset();
    }
  };

  const onSave = form.handleSubmit((values) => {
    if (!blocked) {
      update.mutate(toRepositoryRequest(values), { onSuccess: () => onOpenChange(false) });
    }
  });
  const updateError = update.error ? describeError(update.error) : undefined;

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

        <form className="grid gap-4" noValidate onSubmit={onSave}>
          <RepositoryFields idPrefix="repository-edit" form={form} />
          {updateError && (
            <ErrorText>
              {t(updateError.key)} {updateError.detail}
            </ErrorText>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("stacks.cancel")}
            </Button>
            <Button type="submit" disabled={update.isPending || blocked}>
              {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("repositories.edit.save")}
            </Button>
          </DialogFooter>
        </form>

        <WebhookSection
          name={repository.name}
          hasWebhook={repository.hasWebhook}
          url={webhookUrl(window.location.origin, repository.id)}
          description={t("repositories.webhook.description")}
          create={createWebhook}
          remove={removeWebhook}
        />
      </DialogContent>
    </Dialog>
  );
}
