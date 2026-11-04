import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { hasStatus } from "@/api/client";
import {
  type EnvironmentRole,
  useChangeEnvironmentRoleMutation,
  useGrantEnvironmentRoleMutation,
  useRevokeEnvironmentRoleMutation,
} from "@/api/environments";
import { environmentRolesQuery, stacksQuery } from "@/api/queries";
import { type Role, environmentRoles, rolesFor } from "@/api/users";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PlusIcon, Trash2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { MessageKey } from "@/i18n";

/** The scope value for a role on the whole environment: Radix Select items cannot have an empty value. */
const WHOLE_ENVIRONMENT = "environment";

const grantSchema = z.object({
  userName: z.string().trim().min(1, "environment.roles.userNameRequired" satisfies MessageKey),
  scope: z.string(),
  role: z.enum(environmentRoles),
});

type GrantForm = z.infer<typeof grantSchema>;

const emptyForm: GrantForm = { userName: "", scope: WHOLE_ENVIRONMENT, role: "Viewer" };

/**
 * Roles on one environment and its stacks, for its administrators (ADR-0012). The environment administrator role is
 * shown but only a system administrator may give, change or take it; the aggregator refuses anyone else.
 */
export function EnvironmentRolesSection({ environmentId, isAdmin }: { environmentId: string; isAdmin: boolean }) {
  const { t } = useTranslation();
  const assignments = useQuery(environmentRolesQuery(environmentId));
  const stacks = useQuery(stacksQuery(environmentId));
  const change = useChangeEnvironmentRoleMutation(environmentId);
  const revoke = useRevokeEnvironmentRoleMutation(environmentId);

  const scopeOf = (assignment: EnvironmentRole) => assignment.stackName ?? t("environment.roles.wholeEnvironment");
  // Who may touch an assignment: only administrators deal with the environment administrator role.
  const editable = (role: Role) => isAdmin || role !== "EnvironmentAdmin";

  return (
    <section aria-labelledby="environment-roles" className="grid gap-4 rounded-lg border p-4">
      <div>
        <h2 id="environment-roles" className="font-heading text-lg font-semibold">
          {t("environment.roles.title")}
        </h2>
        <p className="text-muted-foreground mt-1 text-sm">{t("environment.roles.description")}</p>
      </div>

      {assignments.isPending ? (
        <p className="text-muted-foreground text-sm">{t("environment.roles.loading")}</p>
      ) : assignments.isError ? (
        <ErrorText>{t("environment.roles.loadFailed")}</ErrorText>
      ) : assignments.data.length === 0 ? (
        <p className="text-muted-foreground text-sm">{t("environment.roles.empty")}</p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("environment.roles.user")}</TableHead>
              <TableHead className="hidden sm:table-cell">{t("environment.roles.scope")}</TableHead>
              <TableHead>{t("environment.roles.role")}</TableHead>
              <TableHead className="sr-only">{t("environment.roles.revoke")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {assignments.data.map((assignment) => (
              <TableRow key={assignment.id}>
                <TableCell className="font-medium whitespace-normal break-words">
                  {assignment.userName}
                  <span className="text-muted-foreground block text-xs font-normal sm:hidden">{scopeOf(assignment)}</span>
                </TableCell>
                <TableCell className="text-muted-foreground hidden sm:table-cell">{scopeOf(assignment)}</TableCell>
                <TableCell>
                  {editable(assignment.role) ? (
                    <Select
                      value={assignment.role}
                      disabled={change.isPending}
                      onValueChange={(role) => {
                        // Radix calls this with "" before the value matches an item; that is not a choice.
                        if (role === "" || role === assignment.role) return;
                        revoke.reset();
                        change.mutate({ assignmentId: assignment.id, role: role as Role });
                      }}
                    >
                      <SelectTrigger size="sm" className="w-40 sm:w-52" aria-label={t("environment.roles.role")}>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {rolesFor(assignment.stackId)
                          .filter(editable)
                          .map((role) => (
                            <SelectItem key={role} value={role}>
                              {t(`roles.${role}`)}
                            </SelectItem>
                          ))}
                      </SelectContent>
                    </Select>
                  ) : (
                    <span title={t("environment.roles.adminOnly")}>{t(`roles.${assignment.role}`)}</span>
                  )}
                </TableCell>
                <TableCell className="text-right">
                  {editable(assignment.role) && (
                    <ConfirmAction
                      variant="ghost"
                      size="sm"
                      title={t("environment.roles.revokeTitle", { name: assignment.userName })}
                      description={t("environment.roles.revokeDescription", {
                        name: assignment.userName,
                        role: t(`roles.${assignment.role}`),
                        scope: scopeOf(assignment),
                      })}
                      confirmLabel={t("environment.roles.revoke")}
                      disabled={revoke.isPending}
                      onConfirm={() => {
                        change.reset();
                        revoke.mutate(assignment.id);
                      }}
                    >
                      <Trash2Icon data-icon="inline-start" />
                      <span className="sr-only sm:not-sr-only">{t("environment.roles.revoke")}</span>
                    </ConfirmAction>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {change.isError && <ErrorText>{t("environment.roles.changeFailed")}</ErrorText>}
      {revoke.isError && <ErrorText>{t("environment.roles.revokeFailed")}</ErrorText>}

      <GrantForm environmentId={environmentId} isAdmin={isAdmin} stacks={stacks.data ?? []} />
    </section>
  );
}

function GrantForm({
  environmentId,
  isAdmin,
  stacks,
}: {
  environmentId: string;
  isAdmin: boolean;
  stacks: readonly { id: string; name: string }[];
}) {
  const { t } = useTranslation();
  const grant = useGrantEnvironmentRoleMutation(environmentId);
  const {
    control,
    register,
    handleSubmit,
    watch,
    setValue,
    reset,
    formState: { errors },
  } = useForm<GrantForm>({ resolver: zodResolver(grantSchema), defaultValues: emptyForm });
  const scope = watch("scope");
  const offered = rolesFor(scope === WHOLE_ENVIRONMENT ? null : scope).filter((role) => isAdmin || role !== "EnvironmentAdmin");

  const onSubmit = handleSubmit(({ userName, scope, role }) =>
    grant.mutate(
      { userName, stackId: scope === WHOLE_ENVIRONMENT ? null : scope, role },
      { onSuccess: () => reset(emptyForm) },
    ),
  );

  const grantError = grant.error
    ? hasStatus(grant.error, 404)
      ? t("environment.roles.notFound")
      : hasStatus(grant.error, 409)
        ? t("environment.roles.conflict")
        : t("environment.roles.grantFailed")
    : undefined;

  return (
    <form className="grid gap-3 border-t pt-4" noValidate onSubmit={onSubmit}>
      <h3 className="font-medium">{t("environment.roles.grantTitle")}</h3>
      <div className="flex flex-wrap items-end gap-3">
        <div className="grid gap-2">
          <Label htmlFor="environment-role-user">{t("environment.roles.userName")}</Label>
          <Input
            id="environment-role-user"
            className="w-48"
            autoComplete="off"
            aria-invalid={errors.userName ? true : undefined}
            aria-describedby={errors.userName ? "environment-role-user-error" : undefined}
            {...register("userName", { onChange: () => grant.reset() })}
          />
        </div>

        <div className="grid gap-2">
          <Label htmlFor="environment-role-scope">{t("environment.roles.scope")}</Label>
          <Controller
            control={control}
            name="scope"
            render={({ field }) => (
              <Select
                value={field.value}
                onValueChange={(value) => {
                  if (value === "") return;
                  field.onChange(value);
                  // A stack cannot have the environment administrator role.
                  if (value !== WHOLE_ENVIRONMENT && watch("role") === "EnvironmentAdmin") {
                    setValue("role", "Editor");
                  }
                  grant.reset();
                }}
              >
                <SelectTrigger id="environment-role-scope" className="w-56">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={WHOLE_ENVIRONMENT}>{t("environment.roles.wholeEnvironment")}</SelectItem>
                  {stacks.map((stack) => (
                    <SelectItem key={stack.id} value={stack.id}>
                      {stack.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          />
        </div>

        <div className="grid gap-2">
          <Label htmlFor="environment-role-role">{t("environment.roles.role")}</Label>
          <Controller
            control={control}
            name="role"
            render={({ field }) => (
              <Select value={field.value} onValueChange={(value) => value !== "" && field.onChange(value)}>
                <SelectTrigger id="environment-role-role" className="w-52">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {offered.map((role) => (
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
          {t("environment.roles.grant")}
        </Button>
      </div>
      <FieldError id="environment-role-user-error" message={errors.userName?.message} />
      {grantError && <ErrorText>{grantError}</ErrorText>}
    </form>
  );
}
