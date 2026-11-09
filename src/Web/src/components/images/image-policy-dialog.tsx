import { zodResolver } from "@hookform/resolvers/zod";
import { type ReactNode, useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useAddImagePolicyMutation, useChangeImagePolicyMutation } from "@/api/images";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
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
import { Switch } from "@/components/ui/switch";
import {
  type ImagePolicy,
  type ImagePolicyForm,
  emptyPolicyForm,
  imagePolicySchema,
  policyForm,
  toSettings,
} from "@/features/images/model";
import { describeError } from "@/features/stacks/model";

/**
 * Adds an image policy to the stack, or changes one (ADR-0015). The aggregator checks the compose file at the branch
 * head when it saves: a policy that finds no service to update is refused with the reason.
 */
export function ImagePolicyDialog({ stackId, policy, trigger }: { stackId: string; policy?: ImagePolicy; trigger: ReactNode }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const add = useAddImagePolicyMutation(stackId);
  const change = useChangeImagePolicyMutation(stackId, policy?.id ?? "");
  const save = policy ? change : add;
  const initial = policy ? policyForm(policy) : emptyPolicyForm();
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ImagePolicyForm>({ resolver: zodResolver(imagePolicySchema), defaultValues: initial });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset(initial);
      add.reset();
      change.reset();
    }
  };

  const onSubmit = handleSubmit((form) => {
    const done = { onSuccess: () => onOpenChange(false) };
    if (policy) {
      change.mutate(toSettings(form), done);
    } else {
      add.mutate({ image: form.image, ...toSettings(form) }, done);
    }
  });

  const saveError = save.error ? describeError(save.error) : undefined;
  const fieldMessage = (message?: string) => (message ? t(message as never) : undefined);
  const switches = [
    { name: "poll", label: t("images.form.poll"), hint: t("images.form.pollHint") },
    { name: "acceptRequests", label: t("images.form.acceptRequests"), hint: t("images.form.acceptRequestsHint") },
    { name: "pinDigest", label: t("images.form.pinDigest"), hint: t("images.form.pinDigestHint") },
  ] as const;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>{trigger}</DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{policy ? t("images.form.editTitle", { image: policy.image }) : t("images.form.addTitle")}</DialogTitle>
            <DialogDescription>{t("images.form.description")}</DialogDescription>
          </DialogHeader>

          <div className="grid gap-1.5">
            <Label htmlFor="image-policy-image">{t("images.form.image")}</Label>
            <Input
              id="image-policy-image"
              autoComplete="off"
              className="font-mono"
              placeholder="gitea.example.com/team/app/app"
              disabled={policy !== undefined}
              aria-invalid={errors.image ? true : undefined}
              aria-describedby="image-policy-image-hint"
              {...register("image")}
            />
            {errors.image ? (
              <FieldError id="image-policy-image-hint" message={fieldMessage(errors.image.message)} />
            ) : (
              <p id="image-policy-image-hint" className="text-muted-foreground text-sm">
                {t("images.form.imageHint")}
              </p>
            )}
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor="image-policy-pattern">{t("images.form.tagPattern")}</Label>
            <Input
              id="image-policy-pattern"
              autoComplete="off"
              className="font-mono"
              aria-invalid={errors.tagPattern ? true : undefined}
              aria-describedby="image-policy-pattern-hint"
              {...register("tagPattern")}
            />
            {errors.tagPattern ? (
              <FieldError id="image-policy-pattern-hint" message={fieldMessage(errors.tagPattern.message)} />
            ) : (
              <p id="image-policy-pattern-hint" className="text-muted-foreground text-sm">
                {t("images.form.tagPatternHint")}
              </p>
            )}
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor="image-policy-order">{t("images.form.order")}</Label>
            <Controller
              control={control}
              name="order"
              render={({ field }) => (
                <Select value={field.value} onValueChange={(value) => value !== "" && value !== field.value && field.onChange(value)}>
                  <SelectTrigger id="image-policy-order" aria-describedby="image-policy-order-hint">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="ImageCreated">{t("images.order.ImageCreated")}</SelectItem>
                    <SelectItem value="TagTimestamp">{t("images.order.TagTimestamp")}</SelectItem>
                  </SelectContent>
                </Select>
              )}
            />
            <p id="image-policy-order-hint" className="text-muted-foreground text-sm">
              {t("images.form.orderHint")}
            </p>
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor="image-policy-services">{t("images.form.services")}</Label>
            <Input id="image-policy-services" autoComplete="off" className="font-mono" aria-describedby="image-policy-services-hint" {...register("services")} />
            <p id="image-policy-services-hint" className="text-muted-foreground text-sm">
              {t("images.form.servicesHint")}
            </p>
          </div>

          {switches.map((item) => (
            <div key={item.name} className="grid gap-1">
              <div className="flex items-center gap-3">
                <Controller
                  control={control}
                  name={item.name}
                  render={({ field }) => <Switch id={`image-policy-${item.name}`} checked={field.value} onCheckedChange={field.onChange} />}
                />
                <Label htmlFor={`image-policy-${item.name}`}>{item.label}</Label>
              </div>
              <p className="text-muted-foreground text-sm">{item.hint}</p>
            </div>
          ))}

          {saveError && (
            <ErrorText>
              {t(saveError.key)} {saveError.detail}
            </ErrorText>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("stacks.cancel")}
            </Button>
            <Button type="submit" disabled={save.isPending}>
              {save.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {policy ? t("images.form.save") : t("images.form.add")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
