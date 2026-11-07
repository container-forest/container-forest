import { zodResolver } from "@hookform/resolvers/zod";
import { useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { hasStatus } from "@/api/client";
import { userQuery } from "@/api/queries";
import {
  type Role,
  type RoleAssignment,
  type User,
  rolesFor,
  useChangeRoleMutation,
  useRenameUserMutation,
  useRevokeRoleMutation,
  useSetAdminMutation,
  useSetUserActiveMutation,
  useUnlockUserMutation,
} from "@/api/users";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText, FieldError } from "@/components/form-messages";
import {
  ArrowLeftIcon,
  Loader2Icon,
  LockOpenIcon,
  ShieldMinusIcon,
  ShieldPlusIcon,
  Trash2Icon,
  UserCheckIcon,
  UserXIcon,
} from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { UserPasskeysCard } from "@/components/passkeys/user-passkeys-card";
import { UserSigningKeysCard } from "@/components/signing-keys/user-signing-keys-card";
import { UserTokensCard } from "@/components/tokens/user-tokens-card";
import { GrantRoleForm } from "@/components/users/grant-role-form";
import { ResetPasswordDialog } from "@/components/users/reset-password-dialog";
import { UserKindBadge, UserStatusBadge } from "@/components/users/user-badges";
import type { MessageKey } from "@/i18n";
import { formatDate, formatDateTime } from "@/lib/format";
import { userNamePattern, userStatus } from "@/lib/users";
import { cn } from "@/lib/utils";

export const Route = createFileRoute("/_authed/users/$userId")({
  loader: ({ context, params }) => context.queryClient.ensureQueryData(userQuery(params.userId)),
  component: UserScreen,
  errorComponent: UserLoadError,
});

/** One user: name, access, password and roles. Administrators only. */
function UserScreen() {
  const { t, i18n } = useTranslation();
  const { userId } = Route.useParams();
  const { data } = useSuspenseQuery(userQuery(userId));
  const { user, roleAssignments } = data;
  const now = new Date();

  return (
    <div className="mx-auto grid w-full max-w-3xl gap-6">
      <div className="grid gap-3">
        <Link to="/users" className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm">
          <ArrowLeftIcon className="size-4" />
          {t("user.back")}
        </Link>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="font-heading text-2xl font-semibold">{user.userName}</h1>
          <UserKindBadge user={user} />
          <UserStatusBadge user={user} now={now} />
        </div>
        <p className="text-muted-foreground text-sm">
          {t("user.createdAt", { date: formatDateTime(user.createdAt, i18n.resolvedLanguage) })}
        </p>
      </div>

      {userStatus(user, now) === "locked" && <LockoutCard user={user} />}
      <RenameCard key={user.userName} user={user} />
      <AccessCard user={user} />
      <RolesCard user={user} assignments={roleAssignments} />
      <UserPasskeysCard userId={user.id} />
      <UserSigningKeysCard userId={user.id} />
      <UserTokensCard userId={user.id} />
    </div>
  );
}

function LockoutCard({ user }: { user: User }) {
  const { t, i18n } = useTranslation();
  const unlock = useUnlockUserMutation(user.id);

  return (
    <Card>
      <CardContent className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm">{t("user.lockedUntil", { date: formatDateTime(user.lockedOutUntil!, i18n.resolvedLanguage) })}</p>
        <Button variant="outline" disabled={unlock.isPending} onClick={() => unlock.mutate()}>
          {unlock.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <LockOpenIcon data-icon="inline-start" />}
          {t("user.unlock")}
        </Button>
        {unlock.isError && <ErrorText>{t("user.unlockFailed")}</ErrorText>}
      </CardContent>
    </Card>
  );
}

const renameSchema = z.object({
  userName: z.string().trim().regex(userNamePattern, "users.userNameInvalid" satisfies MessageKey),
});

type RenameForm = z.infer<typeof renameSchema>;

/** Keyed by the name, so a successful rename starts the form over from the new name. */
function RenameCard({ user }: { user: User }) {
  const { t } = useTranslation();
  const rename = useRenameUserMutation(user.id);
  const {
    register,
    handleSubmit,
    formState: { errors, isDirty },
  } = useForm<RenameForm>({ resolver: zodResolver(renameSchema), defaultValues: { userName: user.userName } });

  const onSubmit = handleSubmit(({ userName }) => rename.mutate(userName));

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("user.rename.title")}</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="grid gap-2" noValidate onSubmit={onSubmit}>
          <Label htmlFor="rename-user" className="sr-only">
            {t("user.rename.title")}
          </Label>
          <div className="flex gap-2">
            <Input
              id="rename-user"
              className="max-w-xs"
              autoComplete="off"
              aria-invalid={errors.userName ? true : undefined}
              aria-describedby={errors.userName ? "rename-user-error" : undefined}
              {...register("userName")}
            />
            <Button type="submit" disabled={!isDirty || rename.isPending}>
              {rename.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("user.rename.submit")}
            </Button>
          </div>
          <FieldError id="rename-user-error" message={errors.userName?.message} />
          {rename.isError && <ErrorText>{hasStatus(rename.error, 409) ? t("users.userNameTaken") : t("user.rename.failed")}</ErrorText>}
        </form>
      </CardContent>
    </Card>
  );
}

function AccessCard({ user }: { user: User }) {
  const { t } = useTranslation();
  const setActive = useSetUserActiveMutation(user.id);
  const setAdmin = useSetAdminMutation(user.id);
  const [passwordReset, setPasswordReset] = useState(false);
  const pending = setActive.isPending || setAdmin.isPending;

  // 409 means the last active administrator for deactivating or revoking, and a deactivated user for granting.
  const error = setActive.error ?? setAdmin.error;
  const errorText = !error
    ? null
    : hasStatus(error, 409)
      ? setAdmin.error && setAdmin.variables === true
        ? t("user.access.activateFirst")
        : t("user.access.lastAdmin")
      : t("user.access.failed");

  const clearResults = () => {
    setActive.reset();
    setAdmin.reset();
    setPasswordReset(false);
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("user.access.title")}</CardTitle>
        <CardDescription>{t("user.access.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <div className="flex flex-wrap gap-2">
          {user.isActive ? (
            <ConfirmAction
              title={t("user.access.confirmDeactivate.title", { name: user.userName })}
              description={t("user.access.confirmDeactivate.description")}
              confirmLabel={t("user.access.confirmDeactivate.confirm")}
              disabled={pending}
              onConfirm={() => {
                clearResults();
                setActive.mutate(false);
              }}
            >
              <UserXIcon data-icon="inline-start" />
              {t("user.access.deactivate")}
            </ConfirmAction>
          ) : (
            <Button
              variant="outline"
              disabled={pending}
              onClick={() => {
                clearResults();
                setActive.mutate(true);
              }}
            >
              <UserCheckIcon data-icon="inline-start" />
              {t("user.access.activate")}
            </Button>
          )}

          {user.isAdmin ? (
            <ConfirmAction
              title={t("user.access.confirmRevokeAdmin.title", { name: user.userName })}
              description={t("user.access.confirmRevokeAdmin.description")}
              confirmLabel={t("user.access.confirmRevokeAdmin.confirm")}
              disabled={pending}
              onConfirm={() => {
                clearResults();
                setAdmin.mutate(false);
              }}
            >
              <ShieldMinusIcon data-icon="inline-start" />
              {t("user.access.revokeAdmin")}
            </ConfirmAction>
          ) : (
            <Button
              variant="outline"
              disabled={pending}
              onClick={() => {
                clearResults();
                setAdmin.mutate(true);
              }}
            >
              <ShieldPlusIcon data-icon="inline-start" />
              {t("user.access.grantAdmin")}
            </Button>
          )}

          <ResetPasswordDialog
            userId={user.id}
            userName={user.userName}
            onDone={() => {
              clearResults();
              setPasswordReset(true);
            }}
          />
        </div>

        {pending && <Loader2Icon className="text-muted-foreground size-4 animate-spin" aria-hidden />}
        {errorText && <ErrorText>{errorText}</ErrorText>}
        {passwordReset && (
          <p role="status" className="text-sm">
            {t("user.resetPassword.done")}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function RolesCard({ user, assignments }: { user: User; assignments: RoleAssignment[] }) {
  const { t, i18n } = useTranslation();
  const changeRole = useChangeRoleMutation(user.id);
  const revokeRole = useRevokeRoleMutation(user.id);
  const scopeOf = (assignment: RoleAssignment) =>
    assignment.stackName === null ? assignment.environmentName : `${assignment.environmentName} / ${assignment.stackName}`;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("user.roles.title")}</CardTitle>
        <CardDescription>
          {t("user.roles.description")}
          {user.isAdmin && ` ${t("user.roles.adminHasAll")}`}
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("user.roles.environment")}</TableHead>
              <TableHead className="hidden sm:table-cell">{t("user.roles.stack")}</TableHead>
              <TableHead>{t("user.roles.role")}</TableHead>
              <TableHead className="hidden md:table-cell">{t("user.roles.grantedAt")}</TableHead>
              <TableHead className="sr-only">{t("user.roles.actions")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {assignments.length === 0 ? (
              <TableRow>
                <TableCell colSpan={5} className="text-muted-foreground py-6 text-center">
                  {t("user.roles.empty")}
                </TableCell>
              </TableRow>
            ) : (
              assignments.map((assignment) => (
                <TableRow key={assignment.id}>
                  <TableCell className="font-medium whitespace-normal break-words">
                    {assignment.environmentName}
                    {/* Narrow screens have no stack column: the stack goes under the environment. */}
                    <span className="text-muted-foreground block text-xs font-normal sm:hidden">
                      {assignment.stackName ?? t("user.roles.wholeEnvironment")}
                    </span>
                  </TableCell>
                  <TableCell
                    className={cn("hidden sm:table-cell", assignment.stackName === null && "text-muted-foreground")}
                  >
                    {assignment.stackName ?? t("user.roles.wholeEnvironment")}
                  </TableCell>
                  <TableCell>
                    <Select
                      value={assignment.role}
                      disabled={changeRole.isPending}
                      onValueChange={(role) => {
                        revokeRole.reset();
                        changeRole.mutate({ assignmentId: assignment.id, role: role as Role });
                      }}
                    >
                      <SelectTrigger size="sm" className="w-28 sm:w-36" aria-label={t("user.roles.role")}>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {rolesFor(assignment.stackId).map((role) => (
                          <SelectItem key={role} value={role}>
                            {t(`roles.${role}`)}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </TableCell>
                  <TableCell className="text-muted-foreground hidden md:table-cell">{formatDate(assignment.grantedAt, i18n.resolvedLanguage)}</TableCell>
                  <TableCell className="text-right">
                    <ConfirmAction
                      variant="ghost"
                      size="sm"
                      title={t("user.roles.confirmRevoke.title")}
                      description={t("user.roles.confirmRevoke.description", {
                        name: user.userName,
                        role: t(`roles.${assignment.role}`),
                        scope: scopeOf(assignment),
                      })}
                      confirmLabel={t("user.roles.confirmRevoke.confirm")}
                      disabled={revokeRole.isPending}
                      onConfirm={() => {
                        changeRole.reset();
                        revokeRole.mutate(assignment.id);
                      }}
                    >
                      <Trash2Icon data-icon="inline-start" />
                      <span className="sr-only sm:not-sr-only">{t("user.roles.revoke")}</span>
                    </ConfirmAction>
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
        {(changeRole.isError || revokeRole.isError) && <ErrorText>{t("user.roles.failed")}</ErrorText>}
        <GrantRoleForm userId={user.id} assignments={assignments} />
      </CardContent>
    </Card>
  );
}

function UserLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();
  const message = hasStatus(error, 404) ? t("user.notFound") : hasStatus(error, 403) ? t("users.forbidden") : t("user.loadFailed");

  return <ErrorText>{message}</ErrorText>;
}
