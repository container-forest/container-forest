import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { userSigningKeysQuery } from "@/api/queries";
import { useAddUserSigningKeyMutation, useRemoveUserSigningKeyMutation } from "@/api/signing-keys";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, PlusIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { toAddSigningKeyRequest } from "@/features/signing-keys/model";
import { describeError } from "@/features/stacks/model";
import { SigningKeyDialog } from "./signing-key-dialog";
import { SigningKeyTable } from "./signing-key-table";

/**
 * Another user's signing keys on their page: an administrator removes any and adds one for a user without a
 * session, such as a CI account. The key then says who added it, next to every signature it makes.
 */
export function UserSigningKeysCard({ userId }: { userId: string }) {
  const { t } = useTranslation();
  const keys = useQuery(userSigningKeysQuery(userId));
  const add = useAddUserSigningKeyMutation(userId);
  const remove = useRemoveUserSigningKeyMutation(userId);
  const removeError = remove.error ? describeError(remove.error) : undefined;

  return (
    <Card>
      <CardHeader className="flex flex-wrap items-start justify-between gap-3">
        <div className="grid gap-1.5">
          <CardTitle>{t("signingKeys.user.title")}</CardTitle>
          <CardDescription>{t("signingKeys.user.description")}</CardDescription>
        </div>
        <SigningKeyDialog
          trigger={
            <Button variant="outline">
              <PlusIcon data-icon="inline-start" />
              {t("signingKeys.add")}
            </Button>
          }
          title={t("signingKeys.addTitle")}
          description={t("signingKeys.addDescription")}
          failedKey="signingKeys.addFailed"
          onSubmit={(form) => add.mutateAsync(toAddSigningKeyRequest(form))}
        />
      </CardHeader>
      <CardContent className="grid gap-3">
        {keys.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("signingKeys.loading")}
          </p>
        ) : keys.isError ? (
          <ErrorText>{t("signingKeys.loadFailed")}</ErrorText>
        ) : keys.data.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("signingKeys.user.empty")}</p>
        ) : (
          <SigningKeyTable keys={keys.data} removing={remove.isPending} onRemove={(key) => remove.mutate(key.id)} />
        )}
        <div aria-live="polite">
          {removeError && (
            <ErrorText>
              {t("signingKeys.removeFailed")} {t(removeError.key)} {removeError.detail}
            </ErrorText>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
