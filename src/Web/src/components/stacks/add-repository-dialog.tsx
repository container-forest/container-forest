import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { Controller, useForm, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import { useCreateRepositoryMutation } from "@/api/stacks";
import { ErrorText, FieldError } from "@/components/form-messages";
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
import {
  type CreateRepositoryForm,
  createRepositorySchema,
  describeError,
  toCreateRepositoryRequest,
} from "@/features/stacks/model";
import { AccessTokenFields } from "./access-token-fields";

const emptyForm: CreateRepositoryForm = { name: "", url: "", isPrivate: false, userName: "", token: "" };

/** "Add repository": an https repository, public or with an access token; administrators only. */
export function AddRepositoryDialog() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const create = useCreateRepositoryMutation();
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<CreateRepositoryForm>({ resolver: zodResolver(createRepositorySchema), defaultValues: emptyForm });
  const isPrivate = useWatch({ control, name: "isPrivate" });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset(emptyForm);
      create.reset();
    }
  };

  // Closing resets both the form and the mutation, so the token stays in neither.
  const onSubmit = handleSubmit((form) =>
    create.mutate(toCreateRepositoryRequest(form), { onSuccess: () => onOpenChange(false) }),
  );
  const createError = create.error && !hasStatus(create.error, 409) ? describeError(create.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("repositories.new")}
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("repositories.create.title")}</DialogTitle>
            <DialogDescription>{t("repositories.create.description")}</DialogDescription>
          </DialogHeader>
          <div className="grid gap-1.5">
            <Label htmlFor="repository-name">{t("repositories.form.name")}</Label>
            <Input
              id="repository-name"
              autoComplete="off"
              aria-invalid={errors.name ? true : undefined}
              aria-describedby={errors.name ? "repository-name-error" : undefined}
              {...register("name")}
            />
            <FieldError id="repository-name-error" message={errors.name?.message} />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="repository-url">{t("repositories.form.url")}</Label>
            <Input
              id="repository-url"
              type="url"
              autoComplete="off"
              placeholder="https://github.com/org/infra.git"
              aria-invalid={errors.url ? true : undefined}
              aria-describedby={errors.url ? "repository-url-error" : undefined}
              {...register("url")}
            />
            <FieldError id="repository-url-error" message={errors.url?.message} />
          </div>
          <div className="grid gap-1">
            <div className="flex items-center gap-2">
              <Controller
                control={control}
                name="isPrivate"
                render={({ field }) => (
                  <Checkbox
                    id="repository-private"
                    checked={field.value}
                    onCheckedChange={(checked) => field.onChange(checked === true)}
                    onBlur={field.onBlur}
                    ref={field.ref}
                  />
                )}
              />
              <Label htmlFor="repository-private" className="font-normal">
                {t("repositories.create.private")}
              </Label>
            </div>
            <p className="text-muted-foreground text-sm">{t("repositories.create.privateHint")}</p>
          </div>
          {isPrivate && (
            <AccessTokenFields
              idPrefix="repository-create"
              userName={register("userName")}
              token={register("token")}
              errors={{ userName: errors.userName?.message, token: errors.token?.message }}
            />
          )}
          {create.error && hasStatus(create.error, 409) && <ErrorText>{t("repositories.create.nameTaken")}</ErrorText>}
          {createError && (
            <ErrorText>
              {t(createError.key)} {createError.detail}
            </ErrorText>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("stacks.cancel")}
            </Button>
            <Button type="submit" disabled={create.isPending}>
              {create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("repositories.create.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
