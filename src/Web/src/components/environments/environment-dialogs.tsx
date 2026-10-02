import { zodResolver } from "@hookform/resolvers/zod";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { type Control, Controller, type FieldErrors, type UseFormRegister, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import { useCreateEnvironmentMutation, useUpdateEnvironmentMutation } from "@/api/environments";
import { EnrollmentInstructions } from "@/components/environments/enrollment-instructions";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PencilIcon, PlusIcon } from "@/components/icons";
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
  type Environment,
  type EnvironmentForm,
  describeError,
  environmentFormSchema,
} from "@/features/environments/model";

const emptyForm: EnvironmentForm = { name: "", isPrivate: false };

/** The answer that created an environment. Its token exists only here, in this dialog's state. */
interface Created {
  id: string;
  name: string;
  token: string;
  expiresAt: string;
}

/**
 * "New environment": name and privacy, then the first enrollment token with the agent's compose file. Closing the
 * dialog drops the token for good; a new one can be issued on the environment's screen.
 */
export function CreateEnvironmentDialog() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [created, setCreated] = useState<Created>();
  const create = useCreateEnvironmentMutation();
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<EnvironmentForm>({ resolver: zodResolver(environmentFormSchema), defaultValues: emptyForm });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset(emptyForm);
      create.reset();
      setCreated(undefined);
    }
  };

  const onSubmit = handleSubmit((form) =>
    create.mutate(form, {
      onSuccess: (answer) => {
        setCreated({ id: answer.id, name: answer.name, token: answer.enrollmentToken, expiresAt: answer.expiresAt });
        // The answer holds the token; the mutation should not keep it.
        create.reset();
      },
    }),
  );

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("environments.new")}
        </Button>
      </DialogTrigger>
      <DialogContent className={created ? "max-h-[90svh] overflow-y-auto sm:max-w-2xl" : undefined}>
        {created ? (
          <div className="grid gap-4">
            <DialogHeader>
              <DialogTitle>{t("environments.create.createdTitle", { name: created.name })}</DialogTitle>
              <DialogDescription>{t("environments.create.createdDescription")}</DialogDescription>
            </DialogHeader>
            <EnrollmentInstructions environmentName={created.name} token={created.token} expiresAt={created.expiresAt} />
            <DialogFooter>
              <Button asChild variant="outline">
                <Link to="/environments/$environmentId" params={{ environmentId: created.id }}>
                  {t("environments.create.open")}
                </Link>
              </Button>
              <Button type="button" onClick={() => onOpenChange(false)}>
                {t("environments.create.done")}
              </Button>
            </DialogFooter>
          </div>
        ) : (
          <form className="grid gap-4" noValidate onSubmit={onSubmit}>
            <DialogHeader>
              <DialogTitle>{t("environments.create.title")}</DialogTitle>
              <DialogDescription>{t("environments.create.description")}</DialogDescription>
            </DialogHeader>
            <EnvironmentFields idPrefix="new-environment" register={register} control={control} errors={errors} />
            <MutationError error={create.error} />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
                {t("environments.cancel")}
              </Button>
              <Button type="submit" disabled={create.isPending}>
                {create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                {t("environments.create.submit")}
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

/** Renaming and privacy of one environment; administrators only. */
export function EditEnvironmentDialog({ environment }: { environment: Environment }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const update = useUpdateEnvironmentMutation(environment.id);
  const current: EnvironmentForm = { name: environment.name, isPrivate: environment.isPrivate };
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors, isDirty },
  } = useForm<EnvironmentForm>({ resolver: zodResolver(environmentFormSchema), values: current });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset(current);
      update.reset();
    }
  };

  const onSubmit = handleSubmit((form) => update.mutate(form, { onSuccess: () => onOpenChange(false) }));
  const idPrefix = `edit-environment-${environment.id}`;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="icon" title={t("environments.edit.title")} aria-label={`${t("environments.edit.title")}: ${environment.name}`}>
          <PencilIcon />
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("environments.edit.title")}</DialogTitle>
            <DialogDescription>{t("environments.edit.description")}</DialogDescription>
          </DialogHeader>
          <EnvironmentFields idPrefix={idPrefix} register={register} control={control} errors={errors} />
          <MutationError error={update.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("environments.cancel")}
            </Button>
            <Button type="submit" disabled={update.isPending || !isDirty}>
              {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("environments.edit.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function EnvironmentFields({
  idPrefix,
  register,
  control,
  errors,
}: {
  idPrefix: string;
  register: UseFormRegister<EnvironmentForm>;
  control: Control<EnvironmentForm>;
  errors: FieldErrors<EnvironmentForm>;
}) {
  const { t } = useTranslation();

  return (
    <>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-name`}>{t("environments.form.name")}</Label>
        <Input
          id={`${idPrefix}-name`}
          autoComplete="off"
          aria-invalid={errors.name ? true : undefined}
          aria-describedby={errors.name ? `${idPrefix}-name-error` : undefined}
          {...register("name")}
        />
        <FieldError id={`${idPrefix}-name-error`} message={errors.name?.message} />
      </div>
      <div className="grid gap-1">
        <div className="flex items-center gap-2">
          <Controller
            control={control}
            name="isPrivate"
            render={({ field }) => (
              <Checkbox
                id={`${idPrefix}-private`}
                checked={field.value}
                onCheckedChange={(checked) => field.onChange(checked === true)}
                onBlur={field.onBlur}
                ref={field.ref}
              />
            )}
          />
          <Label htmlFor={`${idPrefix}-private`} className="font-normal">
            {t("environments.form.private")}
          </Label>
        </div>
        <p className="text-muted-foreground text-sm">{t("environments.form.privateHint")}</p>
      </div>
    </>
  );
}

/** A taken name is the usual conflict and gets its own text; anything else is explained by status. */
function MutationError({ error }: { error: Error | null }) {
  const { t } = useTranslation();
  if (!error) {
    return null;
  }

  if (hasStatus(error, 409)) {
    return <ErrorText>{t("environments.form.nameTaken")}</ErrorText>;
  }

  const { key, detail } = describeError(error);
  return (
    <ErrorText>
      {t(key)} {detail}
    </ErrorText>
  );
}
