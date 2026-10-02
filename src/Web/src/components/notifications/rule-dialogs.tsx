import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { Controller, useForm, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useCreateRuleMutation, useUpdateRuleMutation } from "@/api/notifications";
import { environmentsQuery, stacksQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, PencilIcon, PlusIcon } from "@/components/icons";
import { EventCheckboxes } from "@/components/notifications/event-checkboxes";
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
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  type NotificationRule,
  type RuleEventsForm,
  type RuleForm,
  describeError,
  emptyRuleForm,
  isKnownKind,
  ruleEventsSchema,
  ruleSchema,
  toCreateRuleRequest,
} from "@/features/notifications/model";

/** Radix Select cannot hold an empty value, so "any" has its own; the form keeps "" for it. */
const ANY = "any";

/** "New rule": which events, and from where (the whole system, one environment or one stack). */
export function CreateRuleDialog({ channelId }: { channelId: string }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const create = useCreateRuleMutation(channelId);
  const {
    control,
    handleSubmit,
    reset,
    setValue,
    formState: { errors },
  } = useForm<RuleForm>({ resolver: zodResolver(ruleSchema), defaultValues: emptyRuleForm });
  const environmentId = useWatch({ control, name: "environmentId" });
  const environments = useQuery({ ...environmentsQuery, enabled: open });
  const stacks = useQuery({ ...stacksQuery(environmentId), enabled: open && environmentId !== "" });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset(emptyRuleForm);
      create.reset();
    }
  };

  const onSubmit = handleSubmit((form) => create.mutate(toCreateRuleRequest(form), { onSuccess: () => onOpenChange(false) }));
  const error = create.error ? describeError(create.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button>
          <PlusIcon data-icon="inline-start" />
          {t("notifications.rules.new")}
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("notifications.rules.create.title")}</DialogTitle>
            <DialogDescription>{t("notifications.rules.create.description")}</DialogDescription>
          </DialogHeader>

          <Controller
            control={control}
            name="events"
            render={({ field }) => (
              <EventCheckboxes idPrefix="new-rule" value={field.value} error={errors.events?.message} onChange={field.onChange} />
            )}
          />

          <div className="grid gap-1.5">
            <Label htmlFor="new-rule-environment">{t("notifications.rules.form.environment")}</Label>
            <Controller
              control={control}
              name="environmentId"
              render={({ field }) => (
                <Select
                  value={field.value || ANY}
                  onValueChange={(value) => {
                    field.onChange(value === ANY ? "" : value);
                    // A stack belongs to one environment: a new environment starts from "all its stacks".
                    setValue("stackId", "");
                  }}
                >
                  <SelectTrigger id="new-rule-environment">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value={ANY}>{t("notifications.rules.form.anyEnvironment")}</SelectItem>
                    {(environments.data ?? []).map((environment) => (
                      <SelectItem key={environment.id} value={environment.id}>
                        {environment.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            />
            {environments.isError && <ErrorText>{t("notifications.rules.form.environmentsFailed")}</ErrorText>}
          </div>

          {environmentId !== "" && (
            <div className="grid gap-1.5">
              <Label htmlFor="new-rule-stack">{t("notifications.rules.form.stack")}</Label>
              <Controller
                control={control}
                name="stackId"
                render={({ field }) => (
                  <Select value={field.value || ANY} onValueChange={(value) => field.onChange(value === ANY ? "" : value)}>
                    <SelectTrigger id="new-rule-stack">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value={ANY}>{t("notifications.rules.form.anyStack")}</SelectItem>
                      {(stacks.data ?? []).map((stack) => (
                        <SelectItem key={stack.id} value={stack.id}>
                          {stack.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
              />
              {stacks.isError && <ErrorText>{t("notifications.rules.form.stacksFailed")}</ErrorText>}
            </div>
          )}

          <p className="text-muted-foreground text-sm">{t("notifications.rules.form.scopeFixed")}</p>

          {error && (
            <ErrorText>
              {t(error.key)} {error.detail}
            </ErrorText>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("notifications.cancel")}
            </Button>
            <Button type="submit" disabled={create.isPending}>
              {create.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("notifications.rules.create.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Changing which events a rule sends; its scope stays (the aggregator does not change it). */
export function EditRuleEventsDialog({ rule }: { rule: NotificationRule }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const update = useUpdateRuleMutation(rule.channelId, rule.id);
  // Kinds this panel does not know are kept as they are: dropping them would change the rule behind the user's back.
  const known = rule.events.filter(isKnownKind);
  const unknown = rule.events.filter((kind) => !isKnownKind(kind));
  const form = useForm<RuleEventsForm>({ resolver: zodResolver(ruleEventsSchema), defaultValues: { events: known } });

  const onOpenChange = (next: boolean) => {
    setOpen(next);
    if (next) {
      form.reset({ events: known });
    } else {
      update.reset();
    }
  };

  const onSubmit = form.handleSubmit(({ events }) =>
    update.mutate({ events: [...events, ...unknown], isEnabled: rule.isEnabled }, { onSuccess: () => onOpenChange(false) }),
  );
  const error = update.error ? describeError(update.error) : undefined;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="icon" aria-label={t("notifications.rules.edit.button")} title={t("notifications.rules.edit.button")}>
          <PencilIcon />
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form className="grid gap-4" noValidate onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{t("notifications.rules.edit.title")}</DialogTitle>
            <DialogDescription>{t("notifications.rules.edit.description")}</DialogDescription>
          </DialogHeader>

          <Controller
            control={form.control}
            name="events"
            render={({ field }) => (
              <EventCheckboxes
                idPrefix={`rule-${rule.id}`}
                value={field.value}
                error={form.formState.errors.events?.message}
                onChange={field.onChange}
              />
            )}
          />

          {error && (
            <ErrorText>
              {t(error.key)} {error.detail}
            </ErrorText>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              {t("notifications.cancel")}
            </Button>
            <Button type="submit" disabled={update.isPending}>
              {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("notifications.rules.edit.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
