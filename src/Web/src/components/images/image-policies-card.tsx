import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCheckImagePolicyMutation, usePinImageMutation, useRemoveImagePolicyMutation, useUnpinImageMutation } from "@/api/images";
import { imagePoliciesQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, PencilIcon, PlusIcon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { type ImagePolicy, type ImageUpdateRecord, type ImageUpdateResult, outcomeKey, policyState } from "@/features/images/model";
import { type Stack, describeError, shortCommit } from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";
import { ImagePolicyDialog } from "./image-policy-dialog";

/**
 * The stack's image policies (ADR-0015): which images the aggregator updates, how it picks the tag, the state of the
 * last check, and the latest update commits. Editors add and change policies, check now, pin and unpin.
 */
export function ImagePoliciesCard({ stack }: { stack: Stack }) {
  const { t, i18n } = useTranslation();
  const policies = useQuery(imagePoliciesQuery(stack.id));
  const canChange = policies.data?.canChange ?? false;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("images.title")}</CardTitle>
        <CardDescription>{t("images.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {policies.isPending && <p className="text-muted-foreground text-sm">{t("images.loading")}</p>}
        {policies.isError && <ErrorText>{t("images.loadFailed")}</ErrorText>}
        {policies.data?.policies.length === 0 && <p className="text-muted-foreground text-sm">{t("images.empty")}</p>}
        {policies.data?.policies.map((policy) => (
          <PolicyItem key={policy.id} stack={stack} policy={policy} canChange={canChange} />
        ))}
        {canChange && !stack.isSystem && (
          <div>
            <ImagePolicyDialog
              stackId={stack.id}
              trigger={
                <Button variant="outline">
                  <PlusIcon data-icon="inline-start" />
                  {t("images.add")}
                </Button>
              }
            />
          </div>
        )}
        {policies.data && policies.data.recentUpdates.length > 0 && (
          <div className="grid gap-2 border-t pt-4">
            <h3 className="text-sm font-medium">{t("images.recent")}</h3>
            <ul className="grid gap-1 text-sm">
              {policies.data.recentUpdates.map((update) => (
                <UpdateLine key={update.id} update={update} language={i18n.resolvedLanguage} />
              ))}
            </ul>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function UpdateLine({ update, language }: { update: ImageUpdateRecord; language?: string }) {
  const { t } = useTranslation();
  const who = update.reason === "User" ? t("images.reason.User", { name: update.requestedBy }) : t(`images.reason.${update.reason}`);
  return (
    <li className="flex flex-wrap gap-x-2">
      <span className="text-muted-foreground">{formatDateTime(update.createdAt, language)}</span>
      <code className="font-mono">{shortCommit(update.commit)}</code>
      <span className="font-mono break-all">{update.tag}</span>
      <span className="text-muted-foreground">{who}</span>
    </li>
  );
}

function PolicyItem({ stack, policy, canChange }: { stack: Stack; policy: ImagePolicy; canChange: boolean }) {
  const { t, i18n } = useTranslation();
  const check = useCheckImagePolicyMutation(stack.id, policy.id);
  const pin = usePinImageMutation(stack.id, policy.id);
  const unpin = useUnpinImageMutation(stack.id, policy.id);
  const remove = useRemoveImagePolicyMutation(stack.id, policy.id);
  const [tag, setTag] = useState("");
  const [result, setResult] = useState<ImageUpdateResult>();
  const state = policyState(policy);
  const busy = check.isPending || pin.isPending || unpin.isPending || remove.isPending;
  const error = check.error ?? pin.error ?? unpin.error ?? remove.error;
  const described = error ? describeError(error) : undefined;

  const reset = () => {
    setResult(undefined);
    for (const mutation of [check, pin, unpin, remove]) {
      mutation.reset();
    }
  };

  const onCheck = (dryRun: boolean) => {
    reset();
    check.mutate(dryRun, { onSuccess: setResult });
  };

  const onPin = () => {
    reset();
    pin.mutate(tag.trim(), { onSuccess: (answer) => (setResult(answer), setTag("")) });
  };

  return (
    <section className="grid gap-3 rounded-md border p-3" aria-label={policy.image}>
      <div className="flex flex-wrap items-center gap-2">
        <code className="font-mono text-sm font-medium break-all">{policy.image}</code>
        <Badge variant={state === "failed" ? "destructive" : state === "pinned" ? "default" : "secondary"}>
          {state === "pinned" ? t("images.state.pinned", { tag: policy.pinnedTag }) : t(`images.state.${state}`)}
        </Badge>
        {policy.poll && <Badge variant="outline">{t("images.triggers.poll")}</Badge>}
        {policy.acceptRequests && <Badge variant="outline">{t("images.triggers.hook")}</Badge>}
        {policy.pinDigest && <Badge variant="outline">{t("images.triggers.digest")}</Badge>}
      </div>

      <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-[auto_1fr]">
        <dt className="text-muted-foreground">{t("images.fields.pattern")}</dt>
        <dd className="font-mono break-all">{policy.tagPattern}</dd>
        <dt className="text-muted-foreground">{t("images.fields.order")}</dt>
        <dd>{t(`images.order.${policy.order}`)}</dd>
        {policy.services.length > 0 && (
          <>
            <dt className="text-muted-foreground">{t("images.fields.services")}</dt>
            <dd className="font-mono">{policy.services.join(", ")}</dd>
          </>
        )}
        <dt className="text-muted-foreground">{t("images.fields.latest")}</dt>
        <dd className="font-mono">{policy.latestTag ?? "—"}</dd>
        <dt className="text-muted-foreground">{t("images.fields.checked")}</dt>
        <dd>{policy.lastCheckedAt ? formatDateTime(policy.lastCheckedAt, i18n.resolvedLanguage) : t("images.state.unchecked")}</dd>
        {policy.pinnedTag && (
          <>
            <dt className="text-muted-foreground">{t("images.fields.pinnedBy")}</dt>
            <dd>{policy.pinnedBy}</dd>
          </>
        )}
      </dl>

      {policy.lastError && <ErrorText>{policy.lastError}</ErrorText>}
      {policy.warning && <p className="text-muted-foreground text-sm">{policy.warning}</p>}

      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => onCheck(true)}>
          {check.isPending && check.variables && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
          {t("images.actions.dryRun")}
        </Button>
        {canChange && (
          <>
            <Button type="button" size="sm" disabled={busy || policy.pinnedTag !== null} onClick={() => onCheck(false)}>
              {check.isPending && !check.variables && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
              {t("images.actions.check")}
            </Button>
            {policy.pinnedTag && (
              <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => (reset(), unpin.mutate())}>
                {t("images.actions.unpin")}
              </Button>
            )}
            <ImagePolicyDialog
              stackId={stack.id}
              policy={policy}
              trigger={
                <Button variant="outline" size="sm" disabled={busy}>
                  <PencilIcon data-icon="inline-start" />
                  {t("images.actions.edit")}
                </Button>
              }
            />
            <ConfirmAction
              title={t("images.remove.title", { image: policy.image })}
              description={t("images.remove.description")}
              confirmLabel={t("images.remove.confirm")}
              disabled={busy}
              onConfirm={() => (reset(), remove.mutate())}
            >
              {t("images.remove.confirm")}
            </ConfirmAction>
          </>
        )}
      </div>

      {canChange && (
        <form
          className="flex flex-wrap items-end gap-2"
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            if (tag.trim().length > 0) {
              onPin();
            }
          }}
        >
          <div className="grid min-w-48 flex-1 gap-1.5">
            <Label htmlFor={`pin-${policy.id}`}>{t("images.pin.label")}</Label>
            <Input id={`pin-${policy.id}`} className="font-mono" autoComplete="off" value={tag} onChange={(event) => setTag(event.target.value)} />
          </div>
          <Button type="submit" variant="outline" size="sm" disabled={busy || tag.trim().length === 0}>
            {pin.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
            {t("images.pin.submit")}
          </Button>
        </form>
      )}

      <div aria-live="polite">
        {result && (
          <p role="status" className="text-sm">
            {t(outcomeKey(result.outcome), { tag: result.targetTag, current: result.currentTag })} {result.detail}
          </p>
        )}
        {described && (
          <ErrorText>
            {t(described.key)} {described.detail}
          </ErrorText>
        )}
      </div>
    </section>
  );
}
