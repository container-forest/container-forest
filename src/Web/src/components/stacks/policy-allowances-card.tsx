import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useSetStackPolicyAllowancesMutation, useSetStackPolicyUnrestrictedMutation } from "@/api/policy";
import { composePolicyQuery } from "@/api/queries";
import { AllowanceList } from "@/components/compose-policy/allowance-list";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { type Stack, describeError } from "@/features/stacks/model";

/**
 * The stack under the compose policy (ADR-0012): its own allowances, and the switch that takes it out of the policy
 * altogether. Everyone who sees the stack sees them; administrators of the system or of the environment change them,
 * since an allowance can hand the stack root on the host.
 */
export function PolicyAllowancesCard({ stack }: { stack: Stack }) {
  const { t } = useTranslation();
  const policy = useQuery(composePolicyQuery(stack.environmentId));
  const allowances = useSetStackPolicyAllowancesMutation(stack.id);
  const unrestricted = useSetStackPolicyUnrestrictedMutation(stack.id);
  const error = allowances.error ?? unrestricted.error;
  const failure = error ? describeError(error) : undefined;
  const canChange = stack.canAdministerEnvironment;
  const isUnrestricted = unrestricted.isPending ? unrestricted.variables : stack.policyUnrestricted;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2">
          {t("stack.policy.title")}
          {stack.policyUnrestricted && <Badge variant="destructive">{t("stack.policy.unrestrictedBadge")}</Badge>}
        </CardTitle>
        <CardDescription>{t("stack.policy.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="grid gap-1">
          <div className="flex items-center gap-3">
            {canChange && !isUnrestricted ? (
              // Taking the stack out of the policy gives its Editors root on the host: ask first.
              <ConfirmAction
                variant="outline"
                size="sm"
                title={t("stack.policy.unrestrictTitle", { name: stack.name })}
                description={t("stack.policy.unrestrictDescription")}
                confirmLabel={t("stack.policy.unrestrictConfirm")}
                disabled={unrestricted.isPending}
                onConfirm={() => {
                  allowances.reset();
                  unrestricted.mutate(true);
                }}
              >
                {t("stack.policy.unrestrict")}
              </ConfirmAction>
            ) : (
              <>
                <Switch
                  id="policy-unrestricted"
                  checked={isUnrestricted}
                  disabled={!canChange || unrestricted.isPending}
                  onCheckedChange={(enabled) => {
                    allowances.reset();
                    unrestricted.mutate(enabled);
                  }}
                />
                <Label htmlFor="policy-unrestricted">{t("stack.policy.unrestrictedLabel")}</Label>
              </>
            )}
          </div>
          <p className="text-muted-foreground text-sm">{t("stack.policy.unrestrictedHint")}</p>
        </div>

        {!stack.policyUnrestricted && (
          <div className="grid gap-2">
            <h3 className="font-medium">{t("stack.policy.label")}</h3>
            <AllowanceList
              allowances={stack.policyAllowances}
              protectedPaths={policy.data?.protectedPaths ?? []}
              canChange={canChange}
              pending={allowances.isPending}
              onChange={(next) => {
                unrestricted.reset();
                allowances.mutate(next);
              }}
            />
          </div>
        )}
        <div aria-live="polite">
          {failure && (
            <ErrorText>
              {t("stack.policy.failed")} {t(failure.key)} {failure.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
