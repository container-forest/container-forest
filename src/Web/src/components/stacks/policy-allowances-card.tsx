import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useSetStackPolicyAllowancesMutation } from "@/api/policy";
import { linesClass } from "@/components/environments/compose-policy-section";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { type AllowancesForm, allowancesSchema, joinLines, parseLines } from "@/features/compose-policy/model";
import { type Stack, describeError } from "@/features/stacks/model";

/**
 * The stack's own exceptions to the compose policy (ADR-0012). Everyone who sees the stack sees them; administrators
 * of the system or of the environment change them, since an allowance can hand the stack root on the host.
 */
export function PolicyAllowancesCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const set = useSetStackPolicyAllowancesMutation(stack.id);
  const [saved, setSaved] = useState(false);
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<AllowancesForm>({
    resolver: zodResolver(allowancesSchema),
    defaultValues: { allowances: joinLines(stack.policyAllowances) },
  });
  const saveError = set.error ? describeError(set.error) : undefined;

  const onSubmit = handleSubmit(({ allowances }) => {
    setSaved(false);
    set.mutate(parseLines(allowances), { onSuccess: () => setSaved(true) });
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.policy.title")}</CardTitle>
        <CardDescription>{t("stack.policy.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        {stack.canAdministerEnvironment ? (
          <form className="grid gap-3" noValidate onSubmit={onSubmit}>
            <Label htmlFor="stack-allowances">{t("stack.policy.label")}</Label>
            <textarea
              id="stack-allowances"
              className={linesClass}
              spellCheck={false}
              aria-invalid={errors.allowances ? true : undefined}
              aria-describedby={errors.allowances ? "stack-allowances-error" : undefined}
              {...register("allowances")}
            />
            <p className="text-muted-foreground text-sm">{t("stack.policy.hint")}</p>
            <FieldError id="stack-allowances-error" message={errors.allowances?.message} />
            <div className="flex flex-wrap items-center gap-3">
              <Button type="submit" disabled={set.isPending}>
                {set.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("stack.policy.save")}
              </Button>
              <div aria-live="polite">
                {saved && <span className="text-muted-foreground text-sm">{t("stack.policy.saved")}</span>}
                {saveError && (
                  <ErrorText>
                    {t("stack.policy.failed")} {t(saveError.key)} {saveError.detail}
                  </ErrorText>
                )}
              </div>
            </div>
          </form>
        ) : stack.policyAllowances.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("stack.policy.none")}</p>
        ) : (
          <ul className="grid gap-1 font-mono text-sm">
            {stack.policyAllowances.map((allowance) => (
              <li key={allowance}>{allowance}</li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
