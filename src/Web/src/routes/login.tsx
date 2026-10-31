import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useRef } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { useLoginMutation } from "@/api/auth";
import { hasStatus, isUnauthorized } from "@/api/client";
import { signInWithPasskey, usePasskeyLoginMutation } from "@/api/passkeys";
import { meQuery, signInMethodsQuery } from "@/api/queries";
import { KeyIcon, Loader2Icon, MessageCircleIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { LanguageSwitcher } from "@/components/language-switcher";
import { Brand } from "@/components/layout/brand";
import { autofillSupported, browserErrorKey, isCancelled, passkeysSupported } from "@/features/passkeys/model";
import type { MessageKey } from "@/i18n";

const searchSchema = z.object({
  // Where to go after signing in. Only same-site paths: an absolute URL here would be an open redirect.
  redirect: z
    .string()
    .refine((value) => value.startsWith("/") && !value.startsWith("//"))
    .optional()
    .catch(undefined),
  // Set by the aggregator when a Discord sign-in did not work out (ADR-0009).
  error: z.literal("discord").optional().catch(undefined),
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
  const { redirect, error: signInError } = Route.useSearch();
  const navigate = useNavigate();
  const login = useLoginMutation();
  const queryClient = useQueryClient();
  const passkeyLogin = usePasskeyLoginMutation();
  const methods = useQuery(signInMethodsQuery);
  const offerPasskeys = methods.data?.passkey === true && passkeysSupported();
  const offerDiscord = methods.data?.discord === true;
  // A browser navigation, not a fetch: Discord's page comes next, and the aggregator brings the browser back.
  const discordHref = `/api/auth/discord/login?redirect=${encodeURIComponent(redirect ?? "/")}`;
  const autofill = useRef<AbortController | null>(null);
  const {
    register,
    control,
    handleSubmit,
    getValues,
    formState: { errors },
  } = useForm<LoginForm>({
    resolver: zodResolver(loginSchema),
    defaultValues: { userName: "", password: "", rememberMe: false },
  });

  const signedIn = () => navigate({ href: redirect ?? "/", replace: true });

  const onSubmit = handleSubmit((form) => login.mutate(form, { onSuccess: signedIn }));

  // Passkeys in the user name field's autofill: the browser's ceremony has to be waiting before the field is
  // focused, so it starts with the page rather than on a click, and stops when the page goes.
  useEffect(() => {
    if (!offerPasskeys) {
      return;
    }

    const controller = new AbortController();
    autofill.current = controller;
    void (async () => {
      if (!(await autofillSupported()) || controller.signal.aborted) {
        return;
      }

      try {
        const me = await signInWithPasskey({ rememberMe: () => getValues("rememberMe"), mediation: "conditional", signal: controller.signal });
        queryClient.removeQueries();
        queryClient.setQueryData(meQuery.queryKey, me);
        void navigate({ href: redirect ?? "/", replace: true });
      } catch {
        // Aborted, cancelled or refused: the form stays for a password or the passkey button.
      }
    })();
    return () => controller.abort();
  }, [offerPasskeys, getValues, navigate, queryClient, redirect]);

  const onPasskey = () => {
    // A browser runs one ceremony at a time: the waiting autofill one gives way to the button's.
    autofill.current?.abort();
    login.reset();
    passkeyLogin.mutate(getValues("rememberMe"), { onSuccess: signedIn });
  };
  const passkeyError = passkeyLogin.error && !isCancelled(passkeyLogin.error) ? passkeyLogin.error : null;

  return (
    <main className="relative flex min-h-svh flex-col items-center justify-center gap-6 p-6">
      <div className="absolute top-4 right-4">
        <LanguageSwitcher />
      </div>
      <Brand className="text-2xl" />
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
                autoComplete={offerPasskeys ? "username webauthn" : "username"}
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
                {isUnauthorized(login.error)
                  ? t("login.errors.invalidCredentials")
                  : hasStatus(login.error, 429)
                    ? t("login.errors.tooManyAttempts")
                    : t("login.errors.failed")}
              </p>
            )}

            <Button type="submit" className="w-full" disabled={login.isPending || passkeyLogin.isPending}>
              {login.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("login.submit")}
            </Button>
          </form>

          {(offerPasskeys || offerDiscord) && (
            <div className="mt-4 grid gap-3">
              <div className="text-muted-foreground flex items-center gap-3 text-xs">
                <span className="bg-border h-px flex-1" />
                {t("login.passkey.or")}
                <span className="bg-border h-px flex-1" />
              </div>
              {offerPasskeys && (
                <Button type="button" variant="outline" className="w-full" disabled={login.isPending || passkeyLogin.isPending} onClick={onPasskey}>
                  {passkeyLogin.isPending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <KeyIcon data-icon="inline-start" />}
                  {t("login.passkey.button")}
                </Button>
              )}
              {offerDiscord && (
                <Button asChild variant="outline" className="w-full">
                  <a href={discordHref}>
                    <MessageCircleIcon data-icon="inline-start" />
                    {t("login.discord.button")}
                  </a>
                </Button>
              )}
              {signInError === "discord" && (
                <p role="alert" className="text-destructive text-sm">
                  {t("login.discord.failed")}
                </p>
              )}
              {passkeyError && (
                <p role="alert" className="text-destructive text-sm">
                  {isUnauthorized(passkeyError)
                    ? t("login.passkey.refused")
                    : (browserErrorKey(passkeyError) && t(browserErrorKey(passkeyError)!)) || t("login.errors.failed")}
                </p>
              )}
            </div>
          )}
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
