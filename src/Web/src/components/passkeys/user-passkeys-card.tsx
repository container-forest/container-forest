import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useRemoveUserPasskeyMutation } from "@/api/passkeys";
import { userPasskeysQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { describeError } from "@/features/stacks/model";
import { PasskeyTable } from "./passkey-table";

/** Another user's passkeys on their page, for an administrator to remove a lost one; adding is the user's own. */
export function UserPasskeysCard({ userId }: { userId: string }) {
  const { t } = useTranslation();
  const passkeys = useQuery(userPasskeysQuery(userId));
  const remove = useRemoveUserPasskeyMutation(userId);
  const removeError = remove.error ? describeError(remove.error) : undefined;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("passkeys.user.title")}</CardTitle>
        <CardDescription>{t("passkeys.user.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {passkeys.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("passkeys.loading")}
          </p>
        ) : passkeys.isError ? (
          <ErrorText>{t("passkeys.loadFailed")}</ErrorText>
        ) : passkeys.data.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("passkeys.user.empty")}</p>
        ) : (
          <PasskeyTable passkeys={passkeys.data} removing={remove.isPending} onRemove={(passkey) => remove.mutate(passkey.id)} />
        )}
        <div aria-live="polite">
          {removeError && (
            <ErrorText>
              {t("passkeys.removeFailed")} {t(removeError.key)} {removeError.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
