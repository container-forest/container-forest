import { zodResolver } from "@hookform/resolvers/zod";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { hasStatus } from "@/api/client";
import { useCreateUserMutation } from "@/api/users";
import { FieldError, ErrorText, PasswordReasons } from "@/components/form-messages";
import { Loader2Icon, PlusIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
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
import type { MessageKey } from "@/i18n";
import { userNamePattern } from "@/lib/users";

// The password policy is the aggregator's: it answers with every reason, which the dialog lists.
const createUserSchema = z.object({
  userName: z.string().trim().regex(userNamePattern, "users.userNameInvalid" satisfies MessageKey),
  password: z.string().min(1, "users.passwordRequired" satisfies MessageKey),
  isAdmin: z.boolean(),
});

type CreateUserForm = z.infer<typeof createUserSchema>;

const emptyForm: CreateUserForm = { userName: "", password: "", isAdmin: false };

/** "New user": creates the account and opens the new user's card, where roles are given. */
export function CreateUserDialog() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const create = useCreateUserMutation();
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<CreateUserForm>({ resolver: zodResolver(createUserSchema), defaultValues: emptyForm });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset(emptyForm);
      create.reset();
    }
  };

  const onSubmit = handleSubmit((form) =>
    create.mutate(form, {
      onSuccess: (user) => {
        onOpenChange(false);
        void navigate({ to: "/users/$userId", params: { userId: user.id } });
      },
    }),
  );

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("users.new")}
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("users.create.title")}</DialogTitle>
            <DialogDescription>{t("users.create.description")}</DialogDescription>
          </DialogHeader>

          <div className="grid gap-2">
            <Label htmlFor="new-user-name">{t("users.create.userName")}</Label>
            <Input
              id="new-user-name"
              autoComplete="off"
              aria-invalid={errors.userName ? true : undefined}
              aria-describedby={errors.userName ? "new-user-name-error" : undefined}
              {...register("userName")}
            />
            <FieldError id="new-user-name-error" message={errors.userName?.message} />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="new-user-password">{t("users.create.password")}</Label>
            <Input
              id="new-user-password"
              type="password"
              autoComplete="new-password"
              aria-invalid={errors.password ? true : undefined}
              aria-describedby={errors.password ? "new-user-password-error" : undefined}
              {...register("password")}
            />
            <FieldError id="new-user-password-error" message={errors.password?.message} />
          </div>

          <div className="flex items-start gap-2">
            <Controller
              control={control}
              name="isAdmin"
              render={({ field }) => (
                <Checkbox
                  id="new-user-admin"
                  checked={field.value}
                  onCheckedChange={(checked) => field.onChange(checked === true)}
                  onBlur={field.onBlur}
                  ref={field.ref}
                />
              )}
            />
            <Label htmlFor="new-user-admin" className="font-normal leading-snug">
              {t("users.create.isAdmin")}
            </Label>
          </div>

          {create.isError &&
            (hasStatus(create.error, 409) ? (
              <ErrorText>{t("users.userNameTaken")}</ErrorText>
            ) : hasStatus(create.error, 400) ? (
              <PasswordReasons error={create.error} heading={t("users.passwordRejected")} />
            ) : (
              <ErrorText>{t("users.create.failed")}</ErrorText>
            ))}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("users.cancel")}
            </Button>
            <Button type="submit" disabled={create.isPending}>
              {create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("users.create.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
