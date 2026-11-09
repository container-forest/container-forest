import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useRemovePushTokenMutation, useSetPushTokenMutation } from "@/api/images";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { AccessTokenFields } from "@/components/git-connections/access-token-fields";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { type AccessTokenForm, type GitConnection, accessTokenSchema, toAccessTokenRequest } from "@/features/git-connections/model";
import { describeError } from "@/features/stacks/model";

const emptyToken: AccessTokenForm = { userName: "", token: "" };

/**
 * The token the aggregator pushes image update commits with (ADR-0015), apart from the read-only access token. Best a
 * service user of the git server that may write only to deployment repositories.
 */
export function PushTokenSection({ connection }: { connection: GitConnection }) {
  const { t } = useTranslation();
  const set = useSetPushTokenMutation(connection.id);
  const remove = useRemovePushTokenMutation(connection.id);
  const [saved, setSaved] = useState(false);
  const form = useForm<AccessTokenForm>({ resolver: zodResolver(accessTokenSchema), defaultValues: emptyToken });

  const onSave = form.handleSubmit((values) => {
    setSaved(false);
    remove.reset();
    set.mutate(toAccessTokenRequest(values), {
      onSuccess: () => {
        setSaved(true);
        form.reset(emptyToken);
        set.reset();
      },
    });
  });

  const error = set.error ?? remove.error;
  const described = error ? describeError(error) : undefined;
  const hasToken = connection.hasPushCredential === true;

  return (
    <section aria-labelledby="connection-push-title" className="grid gap-4 border-t pt-4">
      <div>
        <h3 id="connection-push-title" className="font-heading font-semibold">
          {t("images.push.title")}
        </h3>
        <p className="text-muted-foreground mt-1 text-sm">
          {hasToken
            ? t("images.push.current", { userName: connection.pushUserName ?? t("gitConnections.token.defaultUserName") })
            : t("images.push.none")}
        </p>
      </div>
      <form className="grid gap-4" noValidate onSubmit={onSave}>
        <AccessTokenFields
          idPrefix="connection-push"
          userName={form.register("userName")}
          token={form.register("token")}
          errors={{ userName: form.formState.errors.userName?.message, token: form.formState.errors.token?.message }}
        />
        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={set.isPending}>
            {set.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
            {hasToken ? t("gitConnections.token.replace") : t("gitConnections.token.set")}
          </Button>
          {hasToken && (
            <ConfirmAction
              title={t("images.push.removeTitle", { name: connection.name })}
              description={t("images.push.removeDescription")}
              confirmLabel={t("gitConnections.token.remove")}
              disabled={remove.isPending}
              onConfirm={() => (setSaved(false), remove.mutate())}
            >
              {t("gitConnections.token.remove")}
            </ConfirmAction>
          )}
        </div>
      </form>
      <div aria-live="polite">
        {described && (
          <ErrorText>
            {t(described.key)} {described.detail}
          </ErrorText>
        )}
        {saved && (
          <p role="status" className="text-sm">
            {t("gitConnections.token.saved")}
          </p>
        )}
      </div>
    </section>
  );
}
