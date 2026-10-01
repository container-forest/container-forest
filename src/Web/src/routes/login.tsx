import { zodResolver } from "@hookform/resolvers/zod";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { Controller, useForm } from "react-hook-form";
import { z } from "zod";
import { useLoginMutation } from "@/api/auth";
import { isUnauthorized } from "@/api/client";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

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
// sign-in would tell nothing useful about an existing password.
const loginSchema = z.object({
  userName: z.string().trim().min(1, "Введіть ім'я користувача."),
  password: z.string().min(1, "Введіть пароль."),
  rememberMe: z.boolean(),
});

type LoginForm = z.infer<typeof loginSchema>;

function LoginScreen() {
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
    <main className="grid min-h-svh place-items-center p-6">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle className="text-xl">Вхід</CardTitle>
          <CardDescription>container-forest: керування контейнерами на ваших серверах.</CardDescription>
        </CardHeader>
        <CardContent>
          <form className="grid gap-4" noValidate onSubmit={onSubmit}>
            <div className="grid gap-2">
              <Label htmlFor="userName">Ім'я користувача</Label>
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
              <Label htmlFor="password">Пароль</Label>
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
                Запам'ятати мене
              </Label>
            </div>

            {login.isError && (
              <p role="alert" className="text-destructive text-sm">
                {isUnauthorized(login.error)
                  ? "Неправильне ім'я користувача або пароль."
                  : "Не вдалося ввійти. Перевірте з'єднання й спробуйте ще раз."}
              </p>
            )}

            <Button type="submit" className="w-full" disabled={login.isPending}>
              {login.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              Увійти
            </Button>
          </form>
        </CardContent>
      </Card>
    </main>
  );
}

function FieldError({ id, message }: { id: string; message: string | undefined }) {
  return message ? (
    <p id={id} className="text-destructive text-sm">
      {message}
    </p>
  ) : null;
}
