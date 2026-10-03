import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useCreateRepositoryMutation } from "@/api/stacks";
import { ErrorText } from "@/components/form-messages";
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
import { PUBLIC_REPOSITORY, type RepositoryForm, describeError, repositorySchema, toRepositoryRequest } from "@/features/stacks/model";
import { RepositoryFields, useRepositoryFormBlocked } from "./repository-fields";

const emptyForm: RepositoryForm = { name: "", url: "", connectionId: PUBLIC_REPOSITORY };

/**
 * "Add repository": public, or under a git connection allowed in one of the user's environments. Administrators and
 * editors (ADR-0006); no token here, it belongs to the connection.
 */
export function AddRepositoryDialog() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const create = useCreateRepositoryMutation();
  const form = useForm<RepositoryForm>({ resolver: zodResolver(repositorySchema), defaultValues: emptyForm });
  const blocked = useRepositoryFormBlocked(form);

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      form.reset(emptyForm);
      create.reset();
    }
  };

  const onSubmit = form.handleSubmit((values) => {
    if (!blocked) {
      create.mutate(toRepositoryRequest(values), { onSuccess: () => onOpenChange(false) });
    }
  });
  const createError = create.error ? describeError(create.error) : undefined;

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
          <RepositoryFields idPrefix="repository-create" form={form} />
          {createError && (
            <ErrorText>
              {t(createError.key)} {createError.detail}
            </ErrorText>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("stacks.cancel")}
            </Button>
            <Button type="submit" disabled={create.isPending || blocked}>
              {create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("repositories.create.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
