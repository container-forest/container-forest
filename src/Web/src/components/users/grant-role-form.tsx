import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { hasStatus } from "@/api/client";
import { environmentsQuery, stacksQuery } from "@/api/queries";
import { type RoleAssignment, roles, useGrantRoleMutation } from "@/api/users";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PlusIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { MessageKey } from "@/i18n";
import { takenScopes } from "@/lib/users";

/** The scope value for a role on the whole environment: Radix Select items cannot have an empty value. */
const WHOLE_ENVIRONMENT = "environment";

const grantRoleSchema = z.object({
  environmentId: z.string().min(1, "user.roles.grant.environmentRequired" satisfies MessageKey),
  scope: z.string(),
  role: z.enum(roles),
});

type GrantRoleForm = z.infer<typeof grantRoleSchema>;

const emptyForm: GrantRoleForm = { environmentId: "", scope: WHOLE_ENVIRONMENT, role: "Viewer" };

/**
 * Grants a role on an environment or on one of its stacks. Scopes where the user already has a role are offered
 * disabled: the aggregator allows one role per scope, and the existing one is changed in the table above.
 */
export function GrantRoleForm({ userId, assignments }: { userId: string; assignments: RoleAssignment[] }) {
  const { t } = useTranslation();
  const environments = useQuery(environmentsQuery);
  const grant = useGrantRoleMutation(userId);
  const taken = takenScopes(assignments);
  const {
    control,
    handleSubmit,
    watch,
    setValue,
    reset,
    formState: { errors },
  } = useForm<GrantRoleForm>({ resolver: zodResolver(grantRoleSchema), defaultValues: emptyForm });

  const environmentId = watch("environmentId");
  const stacks = useQuery({ ...stacksQuery(environmentId), enabled: environmentId !== "" });

  const onSubmit = handleSubmit(({ environmentId, scope, role }) =>
    grant.mutate(
      { environmentId, stackId: scope === WHOLE_ENVIRONMENT ? null : scope, role },
      { onSuccess: () => reset(emptyForm) },
    ),
  );

  if (environments.isError) {
    return <ErrorText>{t("user.roles.grant.environmentsFailed")}</ErrorText>;
  }

  if (environments.data?.length === 0) {
    return <p className="text-muted-foreground text-sm">{t("user.roles.grant.noEnvironments")}</p>;
  }

  return (
    <form className="grid gap-3 border-t pt-4" noValidate onSubmit={onSubmit}>
      <h3 className="font-medium">{t("user.roles.grant.title")}</h3>
      <div className="flex flex-wrap items-end gap-3">
        <div className="grid gap-2">
          <Label htmlFor="grant-environment">{t("user.roles.grant.environment")}</Label>
          <Controller
            control={control}
            name="environmentId"
            render={({ field }) => (
              <Select
                value={field.value}
                disabled={environments.isPending}
                onValueChange={(value) => {
                  field.onChange(value);
                  // Stacks belong to one environment: the scope starts over with each environment.
                  setValue("scope", WHOLE_ENVIRONMENT);
                  grant.reset();
                }}
              >
                <SelectTrigger
                  id="grant-environment"
                  className="w-48"
                  aria-invalid={errors.environmentId ? true : undefined}
                  aria-describedby={errors.environmentId ? "grant-environment-error" : undefined}
                >
                  <SelectValue placeholder={t("user.roles.grant.environmentPlaceholder")} />
                </SelectTrigger>
                <SelectContent>
                  {environments.data?.map((environment) => (
                    <SelectItem key={environment.id} value={environment.id}>
                      {environment.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          />
        </div>

        <div className="grid gap-2">
          <Label htmlFor="grant-scope">{t("user.roles.grant.stack")}</Label>
          <Controller
            control={control}
            name="scope"
            render={({ field }) => (
              <Select
                value={field.value}
                disabled={environmentId === "" || stacks.isPending}
                onValueChange={(value) => {
                  field.onChange(value);
                  grant.reset();
                }}
              >
                <SelectTrigger id="grant-scope" className="w-72">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={WHOLE_ENVIRONMENT} disabled={taken.environments.has(environmentId)}>
                    {t("user.roles.grant.wholeEnvironment")}
                    {taken.environments.has(environmentId) && ` (${t("user.roles.grant.taken")})`}
                  </SelectItem>
                  {stacks.data?.map((stack) => (
                    <SelectItem key={stack.id} value={stack.id} disabled={taken.stacks.has(stack.id)}>
                      {stack.name}
                      {taken.stacks.has(stack.id) && ` (${t("user.roles.grant.taken")})`}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          />
        </div>

        <div className="grid gap-2">
          <Label htmlFor="grant-role">{t("user.roles.grant.role")}</Label>
          <Controller
            control={control}
            name="role"
            render={({ field }) => (
              <Select value={field.value} onValueChange={field.onChange}>
                <SelectTrigger id="grant-role" className="w-36">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {roles.map((role) => (
                    <SelectItem key={role} value={role}>
                      {t(`roles.${role}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          />
        </div>

        <Button type="submit" disabled={grant.isPending}>
          {grant.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <PlusIcon data-icon="inline-start" />}
          {t("user.roles.grant.submit")}
        </Button>
      </div>

      <FieldError id="grant-environment-error" message={errors.environmentId?.message} />
      {stacks.isError && <ErrorText>{t("user.roles.grant.stacksFailed")}</ErrorText>}
      {grant.isError && (
        <ErrorText>{hasStatus(grant.error, 409) ? t("user.roles.grant.conflict") : t("user.roles.grant.failed")}</ErrorText>
      )}
    </form>
  );
}
