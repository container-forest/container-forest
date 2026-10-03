import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { Controller, useForm, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import { useCreateGitConnectionMutation } from "@/api/git-connections";
import { ErrorText } from "@/components/form-messages";
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
import { Label } from "@/components/ui/label";
import { type CreateConnectionForm, createConnectionSchema, toCreateConnectionRequest } from "@/features/git-connections/model";
import { describeError } from "@/features/stacks/model";
import { AccessTokenFields } from "./access-token-fields";
import { ConnectionFields } from "./connection-fields";

const emptyForm: CreateConnectionForm = {
  name: "",
  urlPrefix: "",
  allEnvironments: false,
  environmentIds: [],
  withToken: true,
  userName: "",
  token: "",
};

/** "New connection": a URL prefix, an optional token and the environments that may use it; administrators only. */
export function CreateConnectionDialog() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const create = useCreateGitConnectionMutation();
  const form = useForm<CreateConnectionForm>({ resolver: zodResolver(createConnectionSchema), defaultValues: emptyForm });
  const withToken = useWatch({ control: form.control, name: "withToken" });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      // The form and the mutation's variables hold the token: drop both.
      form.reset(emptyForm);
      create.reset();
    }
  };

  const onSubmit = form.handleSubmit((values) =>
    create.mutate(toCreateConnectionRequest(values), { onSuccess: () => onOpenChange(false) }),
  );
  const createError = create.error && !hasStatus(create.error, 409) ? describeError(create.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("gitConnections.new")}
        </Button>
      </DialogTrigger>
      <DialogContent className="max-h-[90dvh] overflow-y-auto">
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("gitConnections.create.title")}</DialogTitle>
            <DialogDescription>{t("gitConnections.create.description")}</DialogDescription>
          </DialogHeader>
          <ConnectionFields idPrefix="connection-create" form={form} />
          <div className="grid gap-1">
            <div className="flex items-center gap-2">
              <Controller
                control={form.control}
                name="withToken"
                render={({ field }) => (
                  <Checkbox
                    id="connection-create-with-token"
                    checked={field.value}
                    onCheckedChange={(checked) => field.onChange(checked === true)}
                    onBlur={field.onBlur}
                    ref={field.ref}
                  />
                )}
              />
              <Label htmlFor="connection-create-with-token" className="font-normal">
                {t("gitConnections.create.withToken")}
              </Label>
            </div>
            <p className="text-muted-foreground text-sm">{t("gitConnections.create.withTokenHint")}</p>
          </div>
          {withToken && (
            <AccessTokenFields
              idPrefix="connection-create"
              userName={form.register("userName")}
              token={form.register("token")}
              errors={{ userName: form.formState.errors.userName?.message, token: form.formState.errors.token?.message }}
            />
          )}
          {create.error && hasStatus(create.error, 409) && <ErrorText>{t("gitConnections.form.nameTaken")}</ErrorText>}
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
              {t("gitConnections.create.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
