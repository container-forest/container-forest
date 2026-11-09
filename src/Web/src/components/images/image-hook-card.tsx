import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCreateImageHookMutation, useRemoveImageHookMutation, useSetImageHookDowngradeMutation } from "@/api/images";
import { imagePoliciesQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { CopyButton } from "@/components/copy-button";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { imageHookCommand, imageHookUrl } from "@/features/images/model";
import { type Stack, describeError } from "@/features/stacks/model";

/**
 * The stack's image hook (ADR-0015): CI calls it after pushing an image, and the aggregator checks the stack's image
 * policies now instead of at the next poll, or updates to an exact tag. The token is shown once and kept only in this
 * component's state.
 */
export function ImageHookCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const policies = useQuery(imagePoliciesQuery(stack.id));
  const create = useCreateImageHookMutation(stack.id);
  const downgrade = useSetImageHookDowngradeMutation(stack.id);
  const remove = useRemoveImageHookMutation(stack.id);
  const [token, setToken] = useState<string>();
  const url = imageHookUrl(window.location.origin, stack.id);
  const image = policies.data?.policies.find((p) => p.acceptRequests)?.image ?? "<image>";
  const command = imageHookCommand(url, image);
  const hasHook = stack.hasImageHook === true;

  const onCreate = () => {
    setToken(undefined);
    remove.reset();
    create.mutate(stack.imageHookAllowsDowngrade === true, {
      onSuccess: (response) => {
        setToken(response.token);
        create.reset();
      },
    });
  };

  const error = create.error ?? downgrade.error ?? remove.error;
  const described = error ? describeError(error) : undefined;
  const createLabel = hasHook ? t("images.hook.renew") : t("images.hook.create");

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("images.hook.title")}</CardTitle>
        <CardDescription>{t("images.hook.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <p className="text-sm">{hasHook ? t("images.hook.on") : t("images.hook.off")}</p>

        {token !== undefined && (
          <div className="grid gap-3 rounded-md border p-3" role="status">
            <p className="text-sm font-medium">{t("stack.hook.once")}</p>
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
            <p className="text-muted-foreground text-sm">{t("images.hook.howTo")}</p>
          </div>
        )}

        {hasHook && (
          <div className="grid gap-1">
            <div className="flex items-center gap-3">
              <Switch
                id="image-hook-downgrade"
                checked={downgrade.isPending ? downgrade.variables : stack.imageHookAllowsDowngrade === true}
                disabled={downgrade.isPending}
                onCheckedChange={(enabled) => downgrade.mutate(enabled)}
              />
              <Label htmlFor="image-hook-downgrade">{t("images.hook.downgrade")}</Label>
            </div>
            <p className="text-muted-foreground text-sm">{t("images.hook.downgradeHint")}</p>
          </div>
        )}

        <div className="flex flex-wrap gap-2">
          {hasHook ? (
            <ConfirmAction
              variant="default"
              title={t("images.hook.renewTitle", { name: stack.name })}
              description={t("stack.hook.renewDescription")}
              confirmLabel={createLabel}
              disabled={create.isPending}
              onConfirm={onCreate}
            >
              {createLabel}
            </ConfirmAction>
          ) : (
            <Button type="button" disabled={create.isPending} onClick={onCreate}>
              {create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {createLabel}
            </Button>
          )}
          {hasHook && (
            <ConfirmAction
              title={t("images.hook.removeTitle", { name: stack.name })}
              description={t("images.hook.removeDescription")}
              confirmLabel={t("stack.hook.remove")}
              disabled={remove.isPending}
              onConfirm={() => (setToken(undefined), create.reset(), remove.mutate())}
            >
              {t("stack.hook.remove")}
            </ConfirmAction>
          )}
        </div>
        <div aria-live="polite">
          {described && (
            <ErrorText>
              {t(described.key)} {described.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
