import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useAddPasskeyMutation, useRemovePasskeyMutation, useRenamePasskeyMutation } from "@/api/passkeys";
import { passkeysQuery } from "@/api/queries";
import { ErrorText, FieldError } from "@/components/form-messages";
import { KeyIcon, Loader2Icon, PencilIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { type Passkey, type PasskeyNameForm, browserErrorKey, isCancelled, passkeyNameSchema, passkeysSupported } from "@/features/passkeys/model";
import { describeError } from "@/features/stacks/model";
import type { MessageKey } from "@/i18n";
import { PasskeyTable } from "./passkey-table";

/** The signed-in user's passkeys on their account screen: add one (the browser's dialog), rename, remove. */
export function PasskeysCard() {
  const { t } = useTranslation();
  const passkeys = useQuery(passkeysQuery);
  const add = useAddPasskeyMutation();
  const remove = useRemovePasskeyMutation();
  const [renaming, setRenaming] = useState<Passkey>();
  const supported = passkeysSupported();
  const form = useForm<PasskeyNameForm>({ resolver: zodResolver(passkeyNameSchema), defaultValues: { name: "" } });

  const onAdd = form.handleSubmit(({ name }) => add.mutate(name, { onSuccess: () => form.reset({ name: "" }) }));
  const addError = add.error && !isCancelled(add.error) ? errorText(add.error) : undefined;
  const removeError = remove.error ? describeError(remove.error) : undefined;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("passkeys.title")}</CardTitle>
        <CardDescription>{t("passkeys.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {passkeys.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("passkeys.loading")}
          </p>
        ) : passkeys.isError ? (
          <ErrorText>{t("passkeys.loadFailed")}</ErrorText>
        ) : passkeys.data.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("passkeys.empty")}</p>
        ) : (
          <PasskeyTable
            passkeys={passkeys.data}
            removing={remove.isPending}
            onRemove={(passkey) => remove.mutate(passkey.id)}
            extraAction={(passkey) => (
              <Button type="button" variant="ghost" size="sm" onClick={() => setRenaming(passkey)}>
                <PencilIcon data-icon="inline-start" />
                {t("passkeys.rename")}
              </Button>
            )}
          />
        )}

        {supported ? (
          <form className="grid gap-2" noValidate onSubmit={onAdd}>
            <Label htmlFor="passkey-name">{t("passkeys.form.name")}</Label>
            <div className="flex flex-wrap gap-2">
              <Input
                id="passkey-name"
                className="max-w-xs"
                autoComplete="off"
                placeholder={t("passkeys.form.namePlaceholder")}
                aria-invalid={form.formState.errors.name ? true : undefined}
                aria-describedby={form.formState.errors.name ? "passkey-name-error" : undefined}
                {...form.register("name", { onChange: () => add.reset() })}
              />
              <Button type="submit" disabled={add.isPending}>
                {add.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <KeyIcon data-icon="inline-start" />}
                {t("passkeys.add")}
              </Button>
            </div>
            <FieldError id="passkey-name-error" message={form.formState.errors.name?.message} />
          </form>
        ) : (
          <p className="text-muted-foreground text-sm">{t("passkeys.unsupported")}</p>
        )}

        <div aria-live="polite">
          {addError && <ErrorText>{addError}</ErrorText>}
          {removeError && (
            <ErrorText>
              {t("passkeys.removeFailed")} {t(removeError.key)} {removeError.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
      <RenameDialog passkey={renaming} onClose={() => setRenaming(undefined)} />
    </Card>
  );

  function errorText(error: unknown): string {
    const browserKey = browserErrorKey(error);
    if (browserKey) {
      return t(browserKey);
    }

    const { key, detail } = describeError(error);
    return `${t("passkeys.addFailed")} ${t(key as MessageKey)} ${detail ?? ""}`.trim();
  }
}

/** Renames a passkey; keyed by nothing, the form starts from the passkey's current name each time it opens. */
function RenameDialog({ passkey, onClose }: { passkey: Passkey | undefined; onClose: () => void }) {
  const { t } = useTranslation();
  const rename = useRenamePasskeyMutation();
  const form = useForm<PasskeyNameForm>({ resolver: zodResolver(passkeyNameSchema), values: { name: passkey?.name ?? "" } });

  const close = () => {
    rename.reset();
    onClose();
  };

  const onSubmit = form.handleSubmit(({ name }) => passkey && rename.mutate({ id: passkey.id, name }, { onSuccess: close }));
  const error = rename.error ? describeError(rename.error) : undefined;

  return (
    <Dialog open={passkey !== undefined} onOpenChange={(open) => !open && close()}>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("passkeys.renameTitle")}</DialogTitle>
            <DialogDescription>{t("passkeys.renameDescription")}</DialogDescription>
          </DialogHeader>
          <div className="grid gap-1.5">
            <Label htmlFor="passkey-rename">{t("passkeys.form.name")}</Label>
            <Input
              id="passkey-rename"
              autoComplete="off"
              aria-invalid={form.formState.errors.name ? true : undefined}
              aria-describedby={form.formState.errors.name ? "passkey-rename-error" : undefined}
              {...form.register("name")}
            />
            <FieldError id="passkey-rename-error" message={form.formState.errors.name?.message} />
          </div>
          {error && (
            <ErrorText>
              {t("passkeys.renameFailed")} {t(error.key)} {error.detail}
            </ErrorText>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close}>
              {t("passkeys.cancel")}
            </Button>
            <Button type="submit" disabled={rename.isPending}>
              {rename.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("passkeys.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
