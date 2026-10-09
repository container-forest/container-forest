import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { Controller, useForm, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { environmentsQuery } from "@/api/queries";
import { useCreateApiTokenMutation } from "@/api/tokens";
import { CopyButton } from "@/components/copy-button";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PlusIcon } from "@/components/icons";
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
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { toggleEnvironment } from "@/features/git-connections/model";
import { describeError } from "@/features/stacks/model";
import {
  type CreateTokenForm,
  TOKEN_LIFETIMES,
  createTokenSchema,
  emptyCreateTokenForm,
  toCreateTokenRequest,
} from "@/features/tokens/model";

/**
 * "New token": name, lifetime, read-only and environments; then the token itself, once. Only this component's state
 * holds the token, so closing the dialog drops it for good.
 */
export function CreateTokenDialog() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [token, setToken] = useState<string>();
  const create = useCreateApiTokenMutation();
  const environments = useQuery({ ...environmentsQuery, enabled: open });
  const form = useForm<CreateTokenForm>({ resolver: zodResolver(createTokenSchema), defaultValues: emptyCreateTokenForm });
  const allEnvironments = useWatch({ control: form.control, name: "allEnvironments" });
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
            <code className="bg-muted rounded-md px-3 py-2 font-mono text-xs break-all">
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

            <div className="grid gap-1.5">
              <Label htmlFor="token-lifetime">{t("tokens.form.lifetime")}</Label>
              <Controller
                control={form.control}
                name="lifetime"
                render={({ field }) => (
                  <Select value={field.value} onValueChange={(value) => value !== "" && field.onChange(value)}>
                    <SelectTrigger id="token-lifetime">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {TOKEN_LIFETIMES.map((lifetime) => (
                        <SelectItem key={lifetime} value={lifetime}>
                          {t(`tokens.lifetimes.${lifetime}`)}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
              />
            </div>

            <div className="grid gap-1">
              <div className="flex items-center gap-2">
                <Controller
                  control={form.control}
                  name="readOnly"
                  render={({ field }) => (
                    <Checkbox
                      id="token-read-only"
                      checked={field.value}
                      onCheckedChange={(checked) => field.onChange(checked === true)}
                      onBlur={field.onBlur}
                      ref={field.ref}
                    />
                  )}
                />
                <Label htmlFor="token-read-only" className="font-normal">
                  {t("tokens.form.readOnly")}
                </Label>
              </div>
              <p className="text-muted-foreground text-sm">{t("tokens.form.readOnlyHint")}</p>
            </div>

            <fieldset className="grid gap-2">
              <legend className="mb-1 text-sm font-medium">{t("tokens.form.environments")}</legend>
              <div className="flex items-center gap-2">
                <Controller
                  control={form.control}
                  name="allEnvironments"
                  render={({ field }) => (
                    <Checkbox
                      id="token-all-environments"
                      checked={field.value}
                      onCheckedChange={(checked) => field.onChange(checked === true)}
                      onBlur={field.onBlur}
                      ref={field.ref}
                    />
                  )}
                />
                <Label htmlFor="token-all-environments" className="font-normal">
                  {t("tokens.form.allEnvironments")}
                </Label>
              </div>
              <p className="text-muted-foreground text-sm">{t("tokens.form.environmentsHint")}</p>
              {!allEnvironments && (
                <Controller
                  control={form.control}
                  name="environmentIds"
                  render={({ field }) => (
                    <div className="grid gap-2 sm:grid-cols-2">
                      {(environments.data ?? []).map((environment) => (
                        <div key={environment.id} className="flex items-center gap-2">
                          <Checkbox
                            id={`token-environment-${environment.id}`}
                            checked={field.value.includes(environment.id)}
                            onCheckedChange={(checked) =>
                              field.onChange(toggleEnvironment(field.value, environment.id, checked === true))
                            }
                          />
                          <Label htmlFor={`token-environment-${environment.id}`} className="font-normal">
                            {environment.name}
                          </Label>
                        </div>
                      ))}
                    </div>
                  )}
                />
              )}
              <FieldError id="token-environments-error" message={errors.environmentIds?.message} />
              {environments.isError && <ErrorText>{t("tokens.form.environmentsFailed")}</ErrorText>}
            </fieldset>

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
