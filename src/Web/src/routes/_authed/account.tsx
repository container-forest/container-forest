import { zodResolver } from "@hookform/resolvers/zod";
import { createFileRoute } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { useChangePasswordMutation } from "@/api/auth";
import { hasStatus } from "@/api/client";
import { ErrorText, FieldError, PasswordReasons } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { PasskeysCard } from "@/components/passkeys/passkeys-card";
import { InstallCard } from "@/components/pwa/install-card";
import { SigningKeysCard } from "@/components/signing-keys/signing-keys-card";
import { TwoFactorCard } from "@/components/two-factor/two-factor-card";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import type { MessageKey } from "@/i18n";

export const Route = createFileRoute("/_authed/account")({
  component: AccountScreen,
});

// Only the repeat is checked here: whether the new password is good enough is the aggregator's policy.
const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1, "account.password.currentRequired" satisfies MessageKey),
    newPassword: z.string().min(1, "account.password.newRequired" satisfies MessageKey),
    confirmPassword: z.string(),
  })
  .refine((form) => form.confirmPassword === form.newPassword, {
    path: ["confirmPassword"],
    message: "account.password.mismatch" satisfies MessageKey,
  });

type ChangePasswordForm = z.infer<typeof changePasswordSchema>;

const emptyForm: ChangePasswordForm = { currentPassword: "", newPassword: "", confirmPassword: "" };

/**
 * The signed-in user's own account: changing the password, two-factor authentication, passkeys, installing the panel
 * as an app. An administrator who must set up two-factor first (ADR-0019) is sent here and sees it at the top.
 */
function AccountScreen() {
  const { t } = useTranslation();
  const { me } = Route.useRouteContext();
  const changePassword = useChangePasswordMutation();
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ChangePasswordForm>({ resolver: zodResolver(changePasswordSchema), defaultValues: emptyForm });

  const onSubmit = handleSubmit(({ currentPassword, newPassword }) =>
    changePassword.mutate({ currentPassword, newPassword }, { onSuccess: () => reset(emptyForm) }),
  );

  const field = (name: keyof ChangePasswordForm) => ({
    id: `account-${name}`,
    type: "password",
    "aria-invalid": errors[name] ? true : undefined,
    "aria-describedby": errors[name] ? `account-${name}-error` : undefined,
    ...register(name, { onChange: () => changePassword.reset() }),
  });

  return (
    <div className="mx-auto grid w-full max-w-2xl gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">{t("account.title")}</h1>
        <p className="text-muted-foreground mt-1">{t("account.description", { name: me.userName })}</p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("account.password.title")}</CardTitle>
          <CardDescription>{t("account.password.description")}</CardDescription>
        </CardHeader>
        <CardContent>
          <form className="grid max-w-sm gap-4" noValidate onSubmit={onSubmit}>
            {/* Lets password managers tie the new password to this account. */}
            <input type="text" name="username" autoComplete="username" value={me.userName} readOnly hidden />

            <div className="grid gap-2">
              <Label htmlFor="account-currentPassword">{t("account.password.current")}</Label>
              <Input autoComplete="current-password" {...field("currentPassword")} />
              <FieldError id="account-currentPassword-error" message={errors.currentPassword?.message} />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="account-newPassword">{t("account.password.new")}</Label>
              <Input autoComplete="new-password" {...field("newPassword")} />
              <FieldError id="account-newPassword-error" message={errors.newPassword?.message} />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="account-confirmPassword">{t("account.password.confirm")}</Label>
              <Input autoComplete="new-password" {...field("confirmPassword")} />
              <FieldError id="account-confirmPassword-error" message={errors.confirmPassword?.message} />
            </div>

            {changePassword.isError &&
              (hasStatus(changePassword.error, 400) ? (
                <PasswordReasons error={changePassword.error} heading={t("account.password.rejected")} />
              ) : hasStatus(changePassword.error, 429) ? (
                <ErrorText>{t("login.errors.tooManyAttempts")}</ErrorText>
              ) : (
                <ErrorText>{t("account.password.failed")}</ErrorText>
              ))}
            {changePassword.isSuccess && (
              <p role="status" className="text-sm">
                {t("account.password.done")}
              </p>
            )}

            <div>
              <Button type="submit" disabled={changePassword.isPending}>
                {changePassword.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("account.password.submit")}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      <TwoFactorCard />

      <PasskeysCard />

      <SigningKeysCard />

      <InstallCard />
    </div>
  );
}
