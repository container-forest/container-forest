import { zodResolver } from "@hookform/resolvers/zod";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { useLoginMutation } from "@/api/auth";
import { isUnauthorized } from "@/api/client";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { LanguageSwitcher } from "@/components/language-switcher";
import type { MessageKey } from "@/i18n";

const searchSchema = z.object({
  // Where to go after signing in. Only same-site paths: an absolute URL here would be an open redirect.
  redirect: z
    .string()
    .refine((value) => value.startsWith("/") && !value.startsWith("//"))
    .optional()
    .catch(undefined),
});

export const Route = createFileRoute("/login")({
  validateSearch: searchSchema,
  component: LoginScreen,
});

// Only presence is checked here: the password policy is the aggregator's, and hinting at it before a
// sign-in would tell nothing useful about an existing password. Messages are translation keys, so a
// shown error follows a language switch.
const loginSchema = z.object({
  userName: z.string().trim().min(1, "login.errors.userNameRequired" satisfies MessageKey),
  password: z.string().min(1, "login.errors.passwordRequired" satisfies MessageKey),
  rememberMe: z.boolean(),
});

type LoginForm = z.infer<typeof loginSchema>;

function LoginScreen() {
  const { t } = useTranslation();
  const { redirect } = Route.useSearch();
  const navigate = useNavigate();
  const login = useLoginMutation();
  const {
    register,
    control,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginForm>({
    resolver: zodResolver(loginSchema),
    defaultValues: { userName: "", password: "", rememberMe: false },
  });

  const onSubmit = handleSubmit((form) =>
    login.mutate(form, {
      onSuccess: () => navigate({ href: redirect ?? "/", replace: true }),
    }),
  );

  return (
    <main className="relative grid min-h-svh place-items-center p-6">
      <div className="absolute top-4 right-4">
        <LanguageSwitcher />
      </div>
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle className="text-xl">{t("login.title")}</CardTitle>
          <CardDescription>{t("login.description")}</CardDescription>
        </CardHeader>
        <CardContent>
          <form className="grid gap-4" noValidate onSubmit={onSubmit}>
            <div className="grid gap-2">
              <Label htmlFor="userName">{t("login.userName")}</Label>
              <Input
                id="userName"
                autoComplete="username"
                autoFocus
                aria-invalid={errors.userName ? true : undefined}
                aria-describedby={errors.userName ? "userName-error" : undefined}
                {...register("userName")}
              />
              <FieldError id="userName-error" message={errors.userName?.message} />
            </div>

            <div className="grid gap-2">
              <Label htmlFor="password">{t("login.password")}</Label>
              <Input
                id="password"
                type="password"
                autoComplete="current-password"
                aria-invalid={errors.password ? true : undefined}
                aria-describedby={errors.password ? "password-error" : undefined}
                {...register("password")}
              />
              <FieldError id="password-error" message={errors.password?.message} />
            </div>

            <div className="flex items-center gap-2">
              <Controller
                control={control}
                name="rememberMe"
                render={({ field }) => (
                  <Checkbox
                    id="rememberMe"
                    checked={field.value}
                    onCheckedChange={(checked) => field.onChange(checked === true)}
                    onBlur={field.onBlur}
                    ref={field.ref}
                  />
                )}
              />
              <Label htmlFor="rememberMe" className="font-normal">
                {t("login.rememberMe")}
              </Label>
            </div>

            {login.isError && (
              <p role="alert" className="text-destructive text-sm">
                {isUnauthorized(login.error) ? t("login.errors.invalidCredentials") : t("login.errors.failed")}
              </p>
            )}

            <Button type="submit" className="w-full" disabled={login.isPending}>
              {login.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("login.submit")}
            </Button>
          </form>
        </CardContent>
      </Card>
    </main>
  );
}

/** Shows a field's validation error; the message is a translation key from the schema. */
function FieldError({ id, message }: { id: string; message: string | undefined }) {
  const { t } = useTranslation();

  return message ? (
    <p id={id} className="text-destructive text-sm">
      {t(message as MessageKey)}
    </p>
  ) : null;
}
