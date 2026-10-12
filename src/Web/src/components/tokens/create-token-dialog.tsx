import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useCreateApiTokenMutation } from "@/api/tokens";
import { CopyButton } from "@/components/copy-button";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PlusIcon } from "@/components/icons";
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
import { describeError } from "@/features/stacks/model";
import { type CreateTokenForm, createTokenSchema, emptyCreateTokenForm, toCreateTokenRequest } from "@/features/tokens/model";
import { TokenOptionsFields } from "./token-options-fields";

/**
 * "New token": name, lifetime, read-only and environments; then the token itself, once. Only this component's state
 * holds the token, so closing the dialog drops it for good.
 */
export function CreateTokenDialog() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [token, setToken] = useState<string>();
  const create = useCreateApiTokenMutation();
  const form = useForm<CreateTokenForm>({ resolver: zodResolver(createTokenSchema), defaultValues: emptyCreateTokenForm });
  const errors = form.formState.errors;

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      setToken(undefined);
      form.reset(emptyCreateTokenForm);
      create.reset();
    }
  };

  const onSubmit = form.handleSubmit((values) =>
    create.mutate(toCreateTokenRequest(values, new Date()), {
      onSuccess: (response) => {
        setToken(response.token);
        // The mutation's data holds the token too: drop it.
        create.reset();
      },
    }),
  );
  const createError = create.error ? describeError(create.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("tokens.new")}
        </Button>
      </DialogTrigger>
      <DialogContent>
        {token !== undefined ? (
          <div className="grid gap-4">
            <DialogHeader>
              <DialogTitle>{t("tokens.created.title")}</DialogTitle>
              <DialogDescription>{t("tokens.created.once")}</DialogDescription>
            </DialogHeader>
            <div className="grid gap-2" role="status">
              <code className="bg-muted rounded-md px-3 py-2 font-mono text-sm break-all select-all">{token}</code>
              <div>
                <CopyButton text={token} label={t("tokens.created.copy")} />
              </div>
            </div>
            <p className="text-muted-foreground text-sm">{t("tokens.created.howTo")}</p>
            <code className="bg-muted rounded-md px-3 py-2 font-mono text-xs break-words">
              curl -H "Authorization: Bearer $CF_TOKEN" {window.location.origin}/api/me
            </code>
            <DialogFooter>
              <Button type="button" onClick={() => onOpenChange(false)}>
                {t("tokens.created.done")}
              </Button>
            </DialogFooter>
          </div>
        ) : (
          <form className="grid gap-4" noValidate onSubmit={onSubmit}>
            <DialogHeader>
              <DialogTitle>{t("tokens.create.title")}</DialogTitle>
              <DialogDescription>{t("tokens.create.description")}</DialogDescription>
            </DialogHeader>

            <div className="grid gap-1.5">
              <Label htmlFor="token-name">{t("tokens.form.name")}</Label>
              <Input
                id="token-name"
                autoComplete="off"
                placeholder={t("tokens.form.namePlaceholder")}
                aria-invalid={errors.name ? true : undefined}
                aria-describedby={errors.name ? "token-name-error" : undefined}
                {...form.register("name")}
              />
              <FieldError id="token-name-error" message={errors.name?.message} />
            </div>

            <TokenOptionsFields idPrefix="token" form={form} enabled={open} />

            {createError && (
              <ErrorText>
                {t("tokens.createFailed")} {t(createError.key)} {createError.detail}
              </ErrorText>
            )}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
                {t("tokens.cancel")}
              </Button>
              <Button type="submit" disabled={create.isPending}>
                {create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("tokens.create.submit")}
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}
