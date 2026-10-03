import type { UseFormRegisterReturn } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { FieldError } from "@/components/form-messages";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

/** User name and token of a git connection; shared by "New connection" and the connection's token form. */
export function AccessTokenFields({
  idPrefix,
  userName,
  token,
  errors,
}: {
  idPrefix: string;
  userName: UseFormRegisterReturn;
  token: UseFormRegisterReturn;
  errors: { userName?: string; token?: string };
}) {
  const { t } = useTranslation();

  return (
    <>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-token`}>{t("gitConnections.token.token")}</Label>
        <Input
          id={`${idPrefix}-token`}
          type="password"
          // Not the user's own password: browsers should neither offer nor save it.
          autoComplete="new-password"
          aria-invalid={errors.token ? true : undefined}
          aria-describedby={`${idPrefix}-token-hint${errors.token ? ` ${idPrefix}-token-error` : ""}`}
          {...token}
        />
        <p id={`${idPrefix}-token-hint`} className="text-muted-foreground text-sm">
          {t("gitConnections.token.tokenHint")}
        </p>
        <FieldError id={`${idPrefix}-token-error`} message={errors.token} />
      </div>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-user`}>{t("gitConnections.token.userName")}</Label>
        <Input
          id={`${idPrefix}-user`}
          autoComplete="off"
          placeholder="x-access-token"
          aria-invalid={errors.userName ? true : undefined}
          aria-describedby={`${idPrefix}-user-hint${errors.userName ? ` ${idPrefix}-user-error` : ""}`}
          {...userName}
        />
        <p id={`${idPrefix}-user-hint`} className="text-muted-foreground text-sm">
          {t("gitConnections.token.userNameHint")}
        </p>
        <FieldError id={`${idPrefix}-user-error`} message={errors.userName} />
      </div>
    </>
  );
}
