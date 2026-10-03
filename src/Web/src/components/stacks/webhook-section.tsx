import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCreateWebhookSecretMutation, useRemoveWebhookSecretMutation } from "@/api/stacks";
import { ConfirmAction } from "@/components/confirm-action";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { type Repository, describeError, webhookUrl } from "@/features/stacks/model";

/**
 * The repository's webhook in the edit dialog: create or renew the secret, or turn the webhook off.
 * The secret is shown once: only this component's state holds it, and the dialog unmounts it when closed.
 */
export function WebhookSection({ repository }: { repository: Repository }) {
  const { t } = useTranslation();
  const create = useCreateWebhookSecretMutation(repository.id);
  const remove = useRemoveWebhookSecretMutation(repository.id);
  const [secret, setSecret] = useState<string>();
  const url = webhookUrl(window.location.origin, repository.id);

  const onCreate = () => {
    setSecret(undefined);
    remove.reset();
    create.mutate(undefined, {
      onSuccess: (response) => {
        setSecret(response.secret);
        // The mutation's data holds the secret too: drop it.
        create.reset();
      },
    });
  };

  const onRemove = () => {
    setSecret(undefined);
    create.reset();
    remove.mutate();
  };

  const createError = create.error ? describeError(create.error) : undefined;
  const removeError = remove.error ? describeError(remove.error) : undefined;
  const createLabel = repository.hasWebhook ? t("repositories.webhook.renew") : t("repositories.webhook.create");
  const createIcon = create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />;

  return (
    <section aria-labelledby="repository-webhook-title" className="grid gap-4 border-t pt-4">
      <div>
        <h3 id="repository-webhook-title" className="font-heading font-semibold">
          {t("repositories.webhook.title")}
        </h3>
        <p className="text-muted-foreground mt-1 text-sm">{t("repositories.webhook.description")}</p>
        <p className="mt-1 text-sm">{repository.hasWebhook ? t("repositories.webhook.on") : t("repositories.webhook.off")}</p>
      </div>

      {secret !== undefined && (
        <div className="grid gap-3 rounded-md border p-3" role="status">
          <p className="text-sm font-medium">{t("repositories.webhook.once")}</p>
          <div className="grid gap-1">
            <span className="text-muted-foreground text-xs">{t("repositories.webhook.url")}</span>
            <code className="font-mono text-xs break-all">{url}</code>
            <div>
              <CopyButton text={url} label={t("repositories.webhook.copyUrl")} />
            </div>
          </div>
          <div className="grid gap-1">
            <span className="text-muted-foreground text-xs">{t("repositories.webhook.secret")}</span>
            <code className="font-mono text-xs break-all">{secret}</code>
            <div>
              <CopyButton text={secret} label={t("repositories.webhook.copySecret")} />
            </div>
          </div>
          <p className="text-muted-foreground text-sm">{t("repositories.webhook.howTo")}</p>
        </div>
      )}

      <div className="flex flex-wrap gap-2">
        {repository.hasWebhook ? (
          <ConfirmAction
            variant="default"
            title={t("repositories.webhook.renewTitle", { name: repository.name })}
            description={t("repositories.webhook.renewDescription")}
            confirmLabel={createLabel}
            disabled={create.isPending}
            onConfirm={onCreate}
          >
            {createIcon}
            {createLabel}
          </ConfirmAction>
        ) : (
          <Button type="button" disabled={create.isPending} onClick={onCreate}>
            {createIcon}
            {createLabel}
          </Button>
        )}
        {repository.hasWebhook && (
          <ConfirmAction
            title={t("repositories.webhook.removeTitle", { name: repository.name })}
            description={t("repositories.webhook.removeDescription")}
            confirmLabel={t("repositories.webhook.remove")}
            disabled={remove.isPending}
            onConfirm={onRemove}
          >
            {t("repositories.webhook.remove")}
          </ConfirmAction>
        )}
      </div>
      <div aria-live="polite">
        {createError && (
          <ErrorText>
            {t("repositories.webhook.createFailed")} {t(createError.key)} {createError.detail}
          </ErrorText>
        )}
        {removeError && (
          <ErrorText>
            {t("repositories.webhook.removeFailed")} {t(removeError.key)} {removeError.detail}
          </ErrorText>
        )}
      </div>
    </section>
  );
}
