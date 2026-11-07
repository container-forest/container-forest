import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { Controller, type UseFormReturn, useForm, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { environmentsQuery } from "@/api/queries";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
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
import { toggleEnvironment } from "@/features/git-connections/model";
import { type SigningKeyForm, emptySigningKeyForm, signingKeySchema } from "@/features/signing-keys/model";
import { describeError } from "@/features/stacks/model";

/**
 * Adding a key (name, public key, environments) or editing one (name and environments only). The caller submits:
 * `onSubmit` resolves when the key is saved and rejects with the aggregator's error, which the dialog shows.
 */
export function SigningKeyDialog({
  trigger,
  title,
  description,
  initial = emptySigningKeyForm,
  editing = false,
  failedKey,
  onSubmit,
}: {
  trigger: ReactNode;
  title: string;
  description: string;
  initial?: SigningKeyForm;
  /** The public key is shown but cannot change: a different key is a new key. */
  editing?: boolean;
  failedKey: "signingKeys.addFailed" | "signingKeys.editFailed";
  onSubmit: (form: SigningKeyForm) => Promise<unknown>;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<unknown>();
  const form = useForm<SigningKeyForm>({ resolver: zodResolver(signingKeySchema), defaultValues: initial });
  const errors = form.formState.errors;
  const idPrefix = editing ? "edit-signing-key" : "signing-key";

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (next) {
      form.reset(initial);
      setError(undefined);
    }
  };

  const submit = form.handleSubmit(async (values) => {
    setError(undefined);
    try {
      await onSubmit(values);
      setOpen(false);
    } catch (failure) {
      setError(failure);
    }
  });
  const shown = error ? describeError(error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>{trigger}</DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={submit}>
          <DialogHeader>
            <DialogTitle>{title}</DialogTitle>
            <DialogDescription>{description}</DialogDescription>
          </DialogHeader>

          <div className="grid gap-1.5">
            <Label htmlFor={`${idPrefix}-name`}>{t("signingKeys.form.name")}</Label>
            <Input
              id={`${idPrefix}-name`}
              autoComplete="off"
              placeholder={t("signingKeys.form.namePlaceholder")}
              aria-invalid={errors.name ? true : undefined}
              aria-describedby={errors.name ? `${idPrefix}-name-error` : undefined}
              {...form.register("name")}
            />
            <FieldError id={`${idPrefix}-name-error`} message={errors.name?.message} />
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor={`${idPrefix}-public-key`}>{t("signingKeys.form.publicKey")}</Label>
            <Input
              id={`${idPrefix}-public-key`}
              className="font-mono"
              autoComplete="off"
              spellCheck={false}
              readOnly={editing}
              placeholder={t("signingKeys.form.publicKeyPlaceholder")}
              aria-invalid={errors.publicKey ? true : undefined}
              aria-describedby={errors.publicKey ? `${idPrefix}-public-key-error` : undefined}
              {...form.register("publicKey")}
            />
            <FieldError id={`${idPrefix}-public-key-error`} message={errors.publicKey?.message} />
          </div>

          <ScopeFields idPrefix={idPrefix} form={form} enabled={open} />

          {shown && (
            <ErrorText>
              {t(failedKey)} {t(shown.key)} {shown.detail}
            </ErrorText>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)}>
              {t("signingKeys.cancel")}
            </Button>
            <Button type="submit" disabled={form.formState.isSubmitting}>
              {form.formState.isSubmitting && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("signingKeys.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Every environment, or the chosen ones; as for API tokens, a limit only narrows what the key's user may do. */
function ScopeFields({ idPrefix, form, enabled }: { idPrefix: string; form: UseFormReturn<SigningKeyForm>; enabled: boolean }) {
  const { t } = useTranslation();
  const environments = useQuery({ ...environmentsQuery, enabled });
  const allEnvironments = useWatch({ control: form.control, name: "allEnvironments" });

  return (
    <fieldset className="grid gap-2">
      <div className="flex items-center gap-2">
        <Controller
          control={form.control}
          name="allEnvironments"
          render={({ field }) => (
            <Checkbox
              id={`${idPrefix}-all-environments`}
              checked={field.value}
              onCheckedChange={(checked) => field.onChange(checked === true)}
              onBlur={field.onBlur}
              ref={field.ref}
            />
          )}
        />
        <Label htmlFor={`${idPrefix}-all-environments`} className="font-normal">
          {t("signingKeys.form.allEnvironments")}
        </Label>
      </div>
      <p className="text-muted-foreground text-sm">{t("signingKeys.form.environmentsHint")}</p>
      {!allEnvironments && (
        <Controller
          control={form.control}
          name="environmentIds"
          render={({ field }) => (
            <div className="grid gap-2 sm:grid-cols-2">
              {(environments.data ?? []).map((environment) => (
                <div key={environment.id} className="flex items-center gap-2">
                  <Checkbox
                    id={`${idPrefix}-environment-${environment.id}`}
                    checked={field.value.includes(environment.id)}
                    onCheckedChange={(checked) => field.onChange(toggleEnvironment(field.value, environment.id, checked === true))}
                  />
                  <Label htmlFor={`${idPrefix}-environment-${environment.id}`} className="font-normal">
                    {environment.name}
                  </Label>
                </div>
              ))}
            </div>
          )}
        />
      )}
      <FieldError id={`${idPrefix}-environments-error`} message={form.formState.errors.environmentIds?.message} />
    </fieldset>
  );
}
