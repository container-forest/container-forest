import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { signingKeysQuery } from "@/api/queries";
import { useAddSigningKeyMutation, useRemoveSigningKeyMutation, useUpdateSigningKeyMutation } from "@/api/signing-keys";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, PencilIcon, PlusIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { type SigningKey, editFormOf, toAddSigningKeyRequest, toUpdateSigningKeyRequest } from "@/features/signing-keys/model";
import { describeError } from "@/features/stacks/model";
import { SigningKeyDialog } from "./signing-key-dialog";
import { SigningKeyTable } from "./signing-key-table";

/** The signed-in user's commit signing keys on "Account" (ADR-0011); only a session manages them. */
export function SigningKeysCard() {
  const { t } = useTranslation();
  const keys = useQuery(signingKeysQuery);
  const add = useAddSigningKeyMutation();
  const remove = useRemoveSigningKeyMutation();
  const removeError = remove.error ? describeError(remove.error) : undefined;

  return (
    <Card>
      <CardHeader className="flex flex-wrap items-start justify-between gap-3">
        <div className="grid gap-1.5">
          <CardTitle>{t("signingKeys.title")}</CardTitle>
          <CardDescription>{t("signingKeys.description")}</CardDescription>
        </div>
        <SigningKeyDialog
          trigger={
            <Button>
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
        <p className="text-muted-foreground font-mono text-xs break-words">{t("signingKeys.setup")}</p>
        {keys.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("signingKeys.loading")}
          </p>
        ) : keys.isError ? (
          <ErrorText>{t("signingKeys.loadFailed")}</ErrorText>
        ) : keys.data.length === 0 ? (
          <p className="text-muted-foreground text-sm">{t("signingKeys.empty")}</p>
        ) : (
          <SigningKeyTable
            keys={keys.data}
            removing={remove.isPending}
            onRemove={(key) => remove.mutate(key.id)}
            extraAction={(key) => <EditKeyButton signingKey={key} />}
          />
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

function EditKeyButton({ signingKey }: { signingKey: SigningKey }) {
  const { t } = useTranslation();
  const update = useUpdateSigningKeyMutation(signingKey.id);

  return (
    <SigningKeyDialog
      trigger={
        <Button variant="outline" size="sm">
          <PencilIcon data-icon="inline-start" />
          {t("signingKeys.edit")}
        </Button>
      }
      title={t("signingKeys.editTitle", { name: signingKey.name })}
      description={t("signingKeys.form.environmentsHint")}
      initial={editFormOf(signingKey)}
      editing
      failedKey="signingKeys.editFailed"
      onSubmit={(form) => update.mutateAsync(toUpdateSigningKeyRequest(form))}
    />
  );
}
