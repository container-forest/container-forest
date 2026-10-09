import { useQuery } from "@tanstack/react-query";
import { Controller, type UseFormReturn, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { environmentsQuery } from "@/api/queries";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { toggleEnvironment } from "@/features/git-connections/model";
import { type CreateTokenForm, TOKEN_LIFETIMES } from "@/features/tokens/model";

/**
 * The limits of a new API token: lifetime, read-only and environments. Shared by "New token" and the CLI login
 * approval, which both create a token of the signed-in user.
 */
export function TokenOptionsFields({
  idPrefix,
  form,
  enabled = true,
}: {
  idPrefix: string;
  form: UseFormReturn<CreateTokenForm>;
  /** Loads the environments only while the fields are on screen. */
  enabled?: boolean;
}) {
  const { t } = useTranslation();
  const environments = useQuery({ ...environmentsQuery, enabled });
  const allEnvironments = useWatch({ control: form.control, name: "allEnvironments" });
  const errors = form.formState.errors;

  return (
    <>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-lifetime`}>{t("tokens.form.lifetime")}</Label>
        <Controller
          control={form.control}
          name="lifetime"
          render={({ field }) => (
            <Select value={field.value} onValueChange={(value) => value !== "" && field.onChange(value)}>
              <SelectTrigger id={`${idPrefix}-lifetime`}>
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
                id={`${idPrefix}-read-only`}
                checked={field.value}
                onCheckedChange={(checked) => field.onChange(checked === true)}
                onBlur={field.onBlur}
                ref={field.ref}
              />
            )}
          />
          <Label htmlFor={`${idPrefix}-read-only`} className="font-normal">
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
                id={`${idPrefix}-all-environments`}
                checked={field.value}
                onCheckedChange={(checked) => field.onChange(checked === true)}
                onBlur={field.onBlur}
                ref={field.ref}
              />
            )}
          />
          <Label htmlFor={`${idPrefix}-all-environments`} className="font-normal">
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
        <FieldError id={`${idPrefix}-environments-error`} message={errors.environmentIds?.message} />
        {environments.isError && <ErrorText>{t("tokens.form.environmentsFailed")}</ErrorText>}
      </fieldset>
    </>
  );
}
