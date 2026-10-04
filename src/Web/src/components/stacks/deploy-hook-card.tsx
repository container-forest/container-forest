import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCreateDeployHookMutation, useRemoveDeployHookMutation } from "@/api/stacks";
import { ConfirmAction } from "@/components/confirm-action";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { type Stack, deployHookCommand, deployHookUrl, describeError } from "@/features/stacks/model";

/**
 * The stack's deploy hook: CI calls it after pushing an image, and the stack is deployed with fresh images. The token
 * is shown once: only this component's state holds it, and leaving the screen drops it.
 */
export function DeployHookCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const create = useCreateDeployHookMutation(stack.id);
  const remove = useRemoveDeployHookMutation(stack.id);
  const [token, setToken] = useState<string>();
  const url = deployHookUrl(window.location.origin, stack.id);
  const command = deployHookCommand(url);

  const onCreate = () => {
    setToken(undefined);
    remove.reset();
    create.mutate(undefined, {
      onSuccess: (response) => {
        setToken(response.token);
        // The mutation's data holds the token too: drop it.
        create.reset();
      },
    });
  };

  const onRemove = () => {
    setToken(undefined);
    create.reset();
    remove.mutate();
  };

  const createError = create.error ? describeError(create.error) : undefined;
  const removeError = remove.error ? describeError(remove.error) : undefined;
  const createLabel = stack.hasDeployHook ? t("stack.hook.renew") : t("stack.hook.create");
  const createIcon = create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.hook.title")}</CardTitle>
        <CardDescription>{t("stack.hook.description", { branch: stack.branch })}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <p className="text-sm">{stack.hasDeployHook ? t("stack.hook.on") : t("stack.hook.off")}</p>

        {token !== undefined && (
          <div className="grid gap-3 rounded-md border p-3" role="status">
            <p className="text-sm font-medium">{t("stack.hook.once")}</p>
            <div className="grid gap-1">
              <span className="text-muted-foreground text-xs">{t("stack.hook.url")}</span>
              <code className="font-mono text-xs break-all">{url}</code>
              <div>
                <CopyButton text={url} label={t("stack.hook.copyUrl")} />
              </div>
            </div>
            <div className="grid gap-1">
              <span className="text-muted-foreground text-xs">{t("stack.hook.token")}</span>
              <code className="font-mono text-xs break-all">{token}</code>
              <div>
                <CopyButton text={token} label={t("stack.hook.copyToken")} />
              </div>
            </div>
            <div className="grid gap-1">
              <span className="text-muted-foreground text-xs">{t("stack.hook.command")}</span>
              <code className="font-mono text-xs break-all">{command}</code>
              <div>
                <CopyButton text={command} label={t("stack.hook.copyCommand")} />
              </div>
            </div>
            <p className="text-muted-foreground text-sm">{t("stack.hook.howTo")}</p>
          </div>
        )}

        <div className="flex flex-wrap gap-2">
          {stack.hasDeployHook ? (
            <ConfirmAction
              variant="default"
              title={t("stack.hook.renewTitle", { name: stack.name })}
              description={t("stack.hook.renewDescription")}
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
          {stack.hasDeployHook && (
            <ConfirmAction
              title={t("stack.hook.removeTitle", { name: stack.name })}
              description={t("stack.hook.removeDescription")}
              confirmLabel={t("stack.hook.remove")}
              disabled={remove.isPending}
              onConfirm={onRemove}
            >
              {t("stack.hook.remove")}
            </ConfirmAction>
          )}
        </div>
        <div aria-live="polite">
          {createError && (
            <ErrorText>
              {t("stack.hook.createFailed")} {t(createError.key)} {createError.detail}
            </ErrorText>
          )}
          {removeError && (
            <ErrorText>
              {t("stack.hook.removeFailed")} {t(removeError.key)} {removeError.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
