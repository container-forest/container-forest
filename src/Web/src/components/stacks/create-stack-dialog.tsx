import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { repositoriesQuery } from "@/api/queries";
import { useCreateStackMutation } from "@/api/stacks";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PlusIcon } from "@/components/icons";
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
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { type CreateStackForm, createStackSchema, describeError } from "@/features/stacks/model";

const emptyForm: CreateStackForm = { name: "", repositoryId: "", branch: "main", composePath: "compose.yaml" };

/** "New stack" in an environment: name and source; deploying it is a separate step on the stack's screen. */
export function CreateStackDialog({ environmentId }: { environmentId: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const repositories = useQuery({ ...repositoriesQuery, enabled: open });
  const create = useCreateStackMutation(environmentId);
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<CreateStackForm>({ resolver: zodResolver(createStackSchema), defaultValues: emptyForm });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset(emptyForm);
      create.reset();
    }
  };

  const onSubmit = handleSubmit((form) =>
    create.mutate(form, {
      onSuccess: (stack) => {
        onOpenChange(false);
        void navigate({ to: "/stacks/$stackId", params: { stackId: stack.id } });
      },
    }),
  );

  const createError = create.error ? describeError(create.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("stacks.new")}
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("stacks.create.title")}</DialogTitle>
            <DialogDescription>{t("stacks.create.description")}</DialogDescription>
          </DialogHeader>

          <div className="grid gap-1.5">
            <Label htmlFor="stack-name">{t("stacks.form.name")}</Label>
            <Input
              id="stack-name"
              autoComplete="off"
              aria-invalid={errors.name ? true : undefined}
              aria-describedby="stack-name-hint"
              {...register("name")}
            />
            {errors.name ? (
              <FieldError id="stack-name-hint" message={errors.name.message} />
            ) : (
              <p id="stack-name-hint" className="text-muted-foreground text-sm">
                {t("stacks.form.nameHint")}
              </p>
            )}
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor="stack-repository">{t("stacks.form.repository")}</Label>
            <Controller
              control={control}
              name="repositoryId"
              render={({ field }) => (
                <Select value={field.value} onValueChange={(value) => value !== "" && field.onChange(value)}>
                  <SelectTrigger
                    id="stack-repository"
                    aria-invalid={errors.repositoryId ? true : undefined}
                    aria-describedby={errors.repositoryId ? "stack-repository-error" : undefined}
                  >
                    <SelectValue placeholder={repositories.isPending ? t("stacks.form.loadingRepositories") : t("stacks.form.chooseRepository")} />
                  </SelectTrigger>
                  <SelectContent>
                    {(repositories.data ?? []).map((repository) => (
                      <SelectItem key={repository.id} value={repository.id}>
                        {repository.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            />
            <FieldError id="stack-repository-error" message={errors.repositoryId?.message} />
            {repositories.isSuccess && repositories.data.length === 0 && (
              <p className="text-muted-foreground text-sm">{t("stacks.form.noRepositories")}</p>
            )}
            {repositories.isError && <ErrorText>{t("stacks.form.repositoriesFailed")}</ErrorText>}
          </div>

          <div className="grid grid-cols-2 items-start gap-3">
            <div className="grid gap-1.5">
              <Label htmlFor="stack-branch">{t("stacks.form.branch")}</Label>
              <Input
                id="stack-branch"
                autoComplete="off"
                aria-invalid={errors.branch ? true : undefined}
                aria-describedby={errors.branch ? "stack-branch-error" : undefined}
                {...register("branch")}
              />
              <FieldError id="stack-branch-error" message={errors.branch?.message} />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="stack-compose-path">{t("stacks.form.composePath")}</Label>
              <Input
                id="stack-compose-path"
                autoComplete="off"
                className="font-mono"
                aria-invalid={errors.composePath ? true : undefined}
                aria-describedby={errors.composePath ? "stack-compose-path-error" : undefined}
                {...register("composePath")}
              />
              <FieldError id="stack-compose-path-error" message={errors.composePath?.message} />
            </div>
          </div>

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
              {t("stacks.create.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
