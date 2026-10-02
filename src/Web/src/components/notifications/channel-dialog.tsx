import { zodResolver } from "@hookform/resolvers/zod";
import { type ReactNode, useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useCreateChannelMutation, useUpdateChannelMutation } from "@/api/notifications";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon, PencilIcon, PlusIcon } from "@/components/icons";
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
import {
  type ChannelForm,
  type NotificationChannel,
  channelSchema,
  describeError,
  digitsOnly,
} from "@/features/notifications/model";

const emptyForm: ChannelForm = { name: "", discordChannelId: "" };

/** "New channel": a name for the panel and the Discord channel the bot posts to. */
export function CreateChannelDialog() {
  const { t } = useTranslation();
  const create = useCreateChannelMutation();

  return (
    <ChannelDialog
      trigger={
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("notifications.channels.new")}
        </Button>
      }
      title={t("notifications.channels.create.title")}
      submitLabel={t("notifications.channels.create.submit")}
      initial={emptyForm}
      idPrefix="new-channel"
      mutation={{
        isPending: create.isPending,
        error: create.error,
        reset: create.reset,
        submit: (form, done) => create.mutate(form, { onSuccess: done }),
      }}
    />
  );
}

/** Renaming a channel or pointing it at another Discord channel; on/off is the switch in the list. */
export function EditChannelDialog({ channel }: { channel: NotificationChannel }) {
  const { t } = useTranslation();
  const update = useUpdateChannelMutation(channel.id);

  return (
    <ChannelDialog
      trigger={
        <Button
          variant="ghost"
          size="icon"
          aria-label={t("notifications.channels.edit.button", { name: channel.name })}
          title={t("notifications.channels.edit.button", { name: channel.name })}
        >
          <PencilIcon />
        </Button>
      }
      title={t("notifications.channels.edit.title")}
      submitLabel={t("notifications.channels.edit.submit")}
      initial={{ name: channel.name, discordChannelId: channel.discordChannelId }}
      idPrefix={`channel-${channel.id}`}
      mutation={{
        isPending: update.isPending,
        error: update.error,
        reset: update.reset,
        submit: (form, done) => update.mutate({ ...form, isEnabled: channel.isEnabled }, { onSuccess: done }),
      }}
    />
  );
}

interface ChannelMutation {
  isPending: boolean;
  error: unknown;
  reset: () => void;
  submit: (form: ChannelForm, done: () => void) => void;
}

function ChannelDialog({
  trigger,
  title,
  submitLabel,
  initial,
  idPrefix,
  mutation,
}: {
  trigger: ReactNode;
  title: string;
  submitLabel: string;
  initial: ChannelForm;
  idPrefix: string;
  mutation: ChannelMutation;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ChannelForm>({ resolver: zodResolver(channelSchema), defaultValues: initial });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (next) {
      // The channel may have changed since the dialog was last open.
      reset(initial);
    } else {
      mutation.reset();
    }
  };

  const onSubmit = handleSubmit((form) => mutation.submit(form, () => onOpenChange(false)));
  const error = mutation.error ? describeError(mutation.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>{trigger}</DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{title}</DialogTitle>
            <DialogDescription>{t("notifications.channels.form.description")}</DialogDescription>
          </DialogHeader>

          <div className="grid gap-1.5">
            <Label htmlFor={`${idPrefix}-name`}>{t("notifications.channels.form.name")}</Label>
            <Input
              id={`${idPrefix}-name`}
              autoComplete="off"
              aria-invalid={errors.name ? true : undefined}
              aria-describedby={errors.name ? `${idPrefix}-name-error` : undefined}
              {...register("name")}
            />
            <FieldError id={`${idPrefix}-name-error`} message={errors.name?.message} />
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor={`${idPrefix}-discord-id`}>{t("notifications.channels.form.discordId")}</Label>
            <Controller
              control={control}
              name="discordChannelId"
              render={({ field }) => (
                <Input
                  id={`${idPrefix}-discord-id`}
                  inputMode="numeric"
                  autoComplete="off"
                  className="font-mono"
                  placeholder="123456789012345678"
                  aria-invalid={errors.discordChannelId ? true : undefined}
                  aria-describedby={`${idPrefix}-discord-id-hint`}
                  value={field.value}
                  onBlur={field.onBlur}
                  onChange={(event) => field.onChange(digitsOnly(event.target.value))}
                />
              )}
            />
            {errors.discordChannelId ? (
              <FieldError id={`${idPrefix}-discord-id-hint`} message={errors.discordChannelId.message} />
            ) : (
              <p id={`${idPrefix}-discord-id-hint`} className="text-muted-foreground text-sm">
                {t("notifications.channels.form.discordIdHint")}
              </p>
            )}
          </div>

          {error && (
            <ErrorText>
              {t(error.key)} {error.detail}
            </ErrorText>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("notifications.cancel")}
            </Button>
            <Button type="submit" disabled={mutation.isPending}>
              {mutation.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {submitLabel}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
