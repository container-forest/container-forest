import { zodResolver } from "@hookform/resolvers/zod";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { FieldError } from "@/components/form-messages";
import { TerminalIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { type UserCodeForm, formatUserCode, userCodeSchema } from "@/features/cli-login/model";

export const Route = createFileRoute("/_authed/cli/device")({
  component: CliDeviceScreen,
});

/** "forest login --device": the person types the code the CLI shows, from any device, and goes on to approve it. */
function CliDeviceScreen() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const form = useForm<UserCodeForm, unknown, { code: string }>({
    resolver: zodResolver(userCodeSchema),
    defaultValues: { code: "" },
  });
  const error = form.formState.errors.code?.message;

  const onSubmit = form.handleSubmit(({ code }) =>
    navigate({ to: "/cli/authorize", search: { code: formatUserCode(code) } }),
  );

  return (
    <div className="grid max-w-md gap-6">
      <div>
        <h1 className="font-heading flex items-center gap-2 text-2xl font-semibold">
          <TerminalIcon className="size-7" />
          {t("cliLogin.title")}
        </h1>
        <p className="text-muted-foreground mt-1">{t("cliLogin.device.description")}</p>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>{t("cliLogin.device.title")}</CardTitle>
          <CardDescription>{t("cliLogin.device.hint")}</CardDescription>
        </CardHeader>
        <CardContent>
          <form className="grid gap-3" noValidate onSubmit={onSubmit}>
            <Label htmlFor="cli-user-code" className="sr-only">
              {t("cliLogin.device.title")}
            </Label>
            <Input
              id="cli-user-code"
              autoComplete="off"
              autoCapitalize="characters"
              spellCheck={false}
              placeholder="WDJB-MJHT"
              className="font-mono text-lg tracking-widest uppercase"
              aria-invalid={error ? true : undefined}
              aria-describedby={error ? "cli-user-code-error" : undefined}
              {...form.register("code")}
            />
            <FieldError id="cli-user-code-error" message={error} />
            <div>
              <Button type="submit">{t("cliLogin.device.submit")}</Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
