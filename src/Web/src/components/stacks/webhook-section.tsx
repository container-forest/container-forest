import type { UseMutationResult } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ConfirmAction } from "@/components/confirm-action";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { describeError } from "@/features/stacks/model";

/**
 * A webhook in an edit dialog, of a repository or of a git connection (an organization-wide one): create or renew
 * the secret, or turn the webhook off. The secret is shown once: only this component's state holds it, and the
 * dialog unmounts it when closed.
 */
export function WebhookSection({
  name,
  hasWebhook,
  url,
  description,
  create,
  remove,
}: {
  /** The repository's or connection's name, for the confirmations. */
  name: string;
  hasWebhook: boolean;
  /** The Payload URL to paste into the git server. */
  url: string;
  description: string;
  create: UseMutationResult<{ secret: string }, Error, void>;
  remove: UseMutationResult<unknown, Error, void>;
}) {
  const { t } = useTranslation();
  const [secret, setSecret] = useState<string>();

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
  const createLabel = hasWebhook ? t("repositories.webhook.renew") : t("repositories.webhook.create");
  const createIcon = create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />;

  return (
    <section aria-labelledby="webhook-title" className="grid gap-4 border-t pt-4">
      <div>
        <h3 id="webhook-title" className="font-heading font-semibold">
          {t("repositories.webhook.title")}
        </h3>
        <p className="text-muted-foreground mt-1 text-sm">{description}</p>
        <p className="mt-1 text-sm">{hasWebhook ? t("repositories.webhook.on") : t("repositories.webhook.off")}</p>
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
        {hasWebhook ? (
          <ConfirmAction
            variant="default"
            title={t("repositories.webhook.renewTitle", { name })}
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
        {hasWebhook && (
          <ConfirmAction
            title={t("repositories.webhook.removeTitle", { name })}
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
