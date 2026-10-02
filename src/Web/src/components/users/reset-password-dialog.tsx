import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { useResetPasswordMutation } from "@/api/users";
import { ErrorText, FieldError, PasswordReasons } from "@/components/form-messages";
import { KeyIcon, Loader2Icon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { hasStatus } from "@/api/client";
import type { MessageKey } from "@/i18n";

const resetPasswordSchema = z.object({
  newPassword: z.string().min(1, "users.passwordRequired" satisfies MessageKey),
});

type ResetPasswordForm = z.infer<typeof resetPasswordSchema>;

/** Sets a password the administrator hands over; `onDone` lets the card say so after the dialog closes. */
export function ResetPasswordDialog({ userId, userName, onDone }: { userId: string; userName: string; onDone: () => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const resetPassword = useResetPasswordMutation(userId);
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ResetPasswordForm>({ resolver: zodResolver(resetPasswordSchema), defaultValues: { newPassword: "" } });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset();
      resetPassword.reset();
    }
  };

  const onSubmit = handleSubmit(({ newPassword }) =>
    resetPassword.mutate(newPassword, {
      onSuccess: () => {
        onOpenChange(false);
        onDone();
      },
    }),
  );

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button variant="outline">
          <KeyIcon data-icon="inline-start" />
          {t("user.resetPassword.button")}
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("user.resetPassword.title", { name: userName })}</DialogTitle>
            <DialogDescription>{t("user.resetPassword.description")}</DialogDescription>
          </DialogHeader>

          <div className="grid gap-2">
            <Label htmlFor="reset-password">{t("user.resetPassword.newPassword")}</Label>
            <Input
              id="reset-password"
              type="password"
              autoComplete="new-password"
              aria-invalid={errors.newPassword ? true : undefined}
              aria-describedby={errors.newPassword ? "reset-password-error" : undefined}
              {...register("newPassword")}
            />
            <FieldError id="reset-password-error" message={errors.newPassword?.message} />
          </div>

          {resetPassword.isError &&
            (hasStatus(resetPassword.error, 400) ? (
              <PasswordReasons error={resetPassword.error} heading={t("users.passwordRejected")} />
            ) : (
              <ErrorText>{t("user.resetPassword.failed")}</ErrorText>
            ))}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("users.cancel")}
            </Button>
            <Button type="submit" disabled={resetPassword.isPending}>
              {resetPassword.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("user.resetPassword.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
