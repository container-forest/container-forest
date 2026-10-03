import { useQuery } from "@tanstack/react-query";
import { Controller, type FieldValues, type Path, type UseFormReturn, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { environmentsQuery } from "@/api/queries";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { type ConnectionDetailsForm, toggleEnvironment } from "@/features/git-connections/model";

/**
 * Name, URL prefix and allowed environments of a git connection; shared by "New connection" and "Edit connection".
 * The environments are the administrator's list, so every one of them is shown.
 */
export function ConnectionFields<TForm extends ConnectionDetailsForm & FieldValues>({
  idPrefix,
  form,
}: {
  idPrefix: string;
  form: UseFormReturn<TForm>;
}) {
  const { t } = useTranslation();
  const environments = useQuery(environmentsQuery);
  // The shared fields, typed for the generic form.
  const name = (field: keyof ConnectionDetailsForm) => field as Path<TForm>;
  const { register, control } = form;
  const errors = form.formState.errors as Partial<Record<keyof ConnectionDetailsForm, { message?: string }>>;
  const allEnvironments = useWatch({ control, name: name("allEnvironments") }) as boolean;

  return (
    <>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-name`}>{t("gitConnections.form.name")}</Label>
        <Input
          id={`${idPrefix}-name`}
          autoComplete="off"
          aria-invalid={errors.name ? true : undefined}
          aria-describedby={errors.name ? `${idPrefix}-name-error` : undefined}
          {...register(name("name"))}
        />
        <FieldError id={`${idPrefix}-name-error`} message={errors.name?.message} />
      </div>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-prefix`}>{t("gitConnections.form.prefix")}</Label>
        <Input
          id={`${idPrefix}-prefix`}
          type="url"
          autoComplete="off"
          placeholder="https://gitea.example.com/team/"
          aria-invalid={errors.urlPrefix ? true : undefined}
          aria-describedby={`${idPrefix}-prefix-hint${errors.urlPrefix ? ` ${idPrefix}-prefix-error` : ""}`}
          {...register(name("urlPrefix"))}
        />
        <p id={`${idPrefix}-prefix-hint`} className="text-muted-foreground text-sm">
          {t("gitConnections.form.prefixHint")}
        </p>
        <FieldError id={`${idPrefix}-prefix-error`} message={errors.urlPrefix?.message} />
      </div>
      <fieldset className="grid gap-2">
        <legend className="mb-1 text-sm font-medium">{t("gitConnections.form.environments")}</legend>
        <div className="flex items-center gap-2">
          <Controller
            control={control}
            name={name("allEnvironments")}
            render={({ field }) => (
              <Checkbox
                id={`${idPrefix}-all`}
                checked={field.value as boolean}
                onCheckedChange={(checked) => field.onChange(checked === true)}
                onBlur={field.onBlur}
                ref={field.ref}
              />
            )}
          />
          <Label htmlFor={`${idPrefix}-all`} className="font-normal">
            {t("gitConnections.form.allEnvironments")}
          </Label>
        </div>
        <p className="text-muted-foreground text-sm">{t("gitConnections.form.environmentsHint")}</p>
        {!allEnvironments && (
          <Controller
            control={control}
            name={name("environmentIds")}
            render={({ field }) => (
              <div className="grid gap-2 sm:grid-cols-2">
                {(environments.data ?? []).map((environment) => (
                  <div key={environment.id} className="flex items-center gap-2">
                    <Checkbox
                      id={`${idPrefix}-environment-${environment.id}`}
                      checked={(field.value as string[]).includes(environment.id)}
                      onCheckedChange={(checked) =>
                        field.onChange(toggleEnvironment(field.value as string[], environment.id, checked === true))
                      }
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
        {environments.isError && <ErrorText>{t("gitConnections.form.environmentsFailed")}</ErrorText>}
      </fieldset>
    </>
  );
}
