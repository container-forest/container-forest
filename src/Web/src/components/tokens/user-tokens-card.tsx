import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { userApiTokensQuery } from "@/api/queries";
import { useRevokeUserApiTokenMutation } from "@/api/tokens";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { describeError } from "@/features/stacks/model";
import { TokenTable } from "./token-table";

/** Another user's API tokens on their page, for an administrator to revoke; new ones only the user creates. */
export function UserTokensCard({ userId }: { userId: string }) {
  const { t } = useTranslation();
  const tokens = useQuery(userApiTokensQuery(userId));
  const revoke = useRevokeUserApiTokenMutation(userId);
  const revokeError = revoke.error ? describeError(revoke.error) : undefined;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("tokens.user.title")}</CardTitle>
        <CardDescription>{t("tokens.user.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {tokens.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("tokens.loading")}
          </p>
        ) : tokens.isError ? (
          <ErrorText>{t("tokens.loadFailed")}</ErrorText>
        ) : tokens.data.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("tokens.user.empty")}</p>
        ) : (
          <TokenTable tokens={tokens.data} revoking={revoke.isPending} onRevoke={(token) => revoke.mutate(token.id)} />
        )}
        <div aria-live="polite">
          {revokeError && (
            <ErrorText>
              {t("tokens.revokeFailed")} {t(revokeError.key)} {revokeError.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
