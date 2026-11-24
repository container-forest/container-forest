import { useTranslation } from "react-i18next";
import { useResetUserTwoFactorMutation } from "@/api/two-factor";
import type { User } from "@/api/users";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

/** For administrators: a user with two-factor on who lost their phone and their recovery codes (ADR-0019). */
export function UserTwoFactorCard({ user }: { user: User }) {
  const { t } = useTranslation();
  const reset = useResetUserTwoFactorMutation(user.id);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("user.twoFactor.title")}</CardTitle>
        <CardDescription>{t("user.twoFactor.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-2">
        <div>
          <ConfirmAction
            title={t("user.twoFactor.confirmTitle", { name: user.userName })}
            description={t("user.twoFactor.confirmDescription")}
            confirmLabel={t("user.twoFactor.reset")}
            disabled={reset.isPending}
            onConfirm={() => reset.mutate()}
          >
            {reset.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
            {t("user.twoFactor.reset")}
          </ConfirmAction>
        </div>
        <div aria-live="polite">{reset.isError && <ErrorText>{t("user.twoFactor.failed")}</ErrorText>}</div>
      </CardContent>
    </Card>
  );
}
