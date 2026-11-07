import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { useSetStackPolicyAllowancesMutation, useUpdateComposePolicyMutation } from "@/api/policy";
import { AllowanceList } from "@/components/compose-policy/allowance-list";
import { composePolicyQuery, composePolicyReportQuery } from "@/api/queries";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  type ComposePolicy,
  type PolicyForm,
  type StackPolicyReport,
  formOf,
  parseLines,
  policyModes,
  policySchema,
  sortReport,
} from "@/features/compose-policy/model";
import { describeError } from "@/features/environments/model";
import { cn } from "@/lib/utils";

/** A plain multi-line field, one entry per line; the panel has no textarea component of its own. */
export const linesClass = cn(
  "border-input bg-transparent dark:bg-input/30 placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-ring/50",
  "min-h-24 w-full rounded-md border px-3 py-2 font-mono text-sm shadow-xs outline-none focus-visible:ring-[3px]",
);

/** The environment's compose policy (ADR-0012) and how its deployed stacks fare under it. */
export function ComposePolicySection({ environmentId }: { environmentId: string }) {
  const { t } = useTranslation();
  const policy = useQuery(composePolicyQuery(environmentId));

  return (
    <section aria-labelledby="compose-policy" className="grid gap-4 rounded-lg border p-4">
      <div>
        <h2 id="compose-policy" className="font-heading text-lg font-semibold">
          {t("composePolicy.title")}
        </h2>
        <p className="text-muted-foreground mt-1 text-sm">{t("composePolicy.description")}</p>
      </div>

      {policy.isPending ? (
        <p className="text-muted-foreground text-sm">{t("composePolicy.loading")}</p>
      ) : policy.isError ? (
        <ErrorText>{t("composePolicy.loadFailed")}</ErrorText>
      ) : (
        <>
          {/* A new form per saved version: the fields start from what the aggregator stored. */}
          <PolicyEditor key={JSON.stringify(policy.data)} environmentId={environmentId} policy={policy.data} />
          <EnvironmentAllowances environmentId={environmentId} policy={policy.data} />
          <PolicyReport environmentId={environmentId} policy={policy.data} />
        </>
      )}
    </section>
  );
}

function PolicyEditor({ environmentId, policy }: { environmentId: string; policy: ComposePolicy }) {
  const { t } = useTranslation();
  const update = useUpdateComposePolicyMutation(environmentId);
  const [saved, setSaved] = useState(false);
  const {
    control,
    register,
    handleSubmit,
    watch,
    formState: { errors },
  } = useForm<PolicyForm>({ resolver: zodResolver(policySchema), defaultValues: formOf(policy) });
  const mode = watch("mode");
  const readOnly = !policy.canChange;
  const saveError = update.error ? describeError(update.error) : undefined;

  const onSubmit = handleSubmit((form) => {
    setSaved(false);
    update.mutate(
      {
        mode: form.mode,
        dataRoot: form.dataRoot.trim(),
        systemReadOnlyPaths: parseLines(form.systemReadOnlyPaths),
        allowances: policy.allowances,
      },
      { onSuccess: () => setSaved(true) },
    );
  });

  return (
    <form className="grid gap-4" noValidate onSubmit={onSubmit}>
      <div className="grid gap-2">
        <Label htmlFor="policy-mode">{t("composePolicy.mode")}</Label>
        <Controller
          control={control}
          name="mode"
          render={({ field }) => (
            <Select value={field.value} disabled={readOnly} onValueChange={(value) => value !== "" && field.onChange(value)}>
              <SelectTrigger id="policy-mode" className="w-full sm:w-96">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {policyModes.map((value) => (
                  <SelectItem key={value} value={value}>
                    {t(`composePolicy.modes.${value}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        />
        <p className="text-muted-foreground text-sm">{t(`composePolicy.modeHints.${mode}`)}</p>
      </div>

      <div className="grid gap-2">
        <Label htmlFor="policy-data-root">{t("composePolicy.dataRoot")}</Label>
        <Input
          id="policy-data-root"
          className="font-mono sm:w-96"
          disabled={readOnly}
          aria-invalid={errors.dataRoot ? true : undefined}
          {...register("dataRoot")}
        />
        <p className="text-muted-foreground text-sm">{t("composePolicy.dataRootHint")}</p>
        <FieldError id="policy-data-root-error" message={errors.dataRoot?.message} />
      </div>

      <div className="grid gap-2">
        <Label htmlFor="policy-system-paths">{t("composePolicy.systemPaths")}</Label>
        <textarea
          id="policy-system-paths"
          className={linesClass}
          disabled={readOnly}
          spellCheck={false}
          aria-invalid={errors.systemReadOnlyPaths ? true : undefined}
          {...register("systemReadOnlyPaths")}
        />
        <p className="text-muted-foreground text-sm">{t("composePolicy.systemPathsHint")}</p>
        <FieldError id="policy-system-paths-error" message={errors.systemReadOnlyPaths?.message} />
      </div>

      <details className="text-sm">
        <summary className="cursor-pointer font-medium">{t("composePolicy.protectedPaths")}</summary>
        <p className="text-muted-foreground mt-2">{t("composePolicy.protectedPathsHint")}</p>
        <p className="mt-2 font-mono text-xs break-words">{policy.protectedPaths.join("  ")}</p>
      </details>

      {readOnly ? (
        <p className="text-muted-foreground text-sm">{t("composePolicy.readOnly")}</p>
      ) : (
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" disabled={update.isPending}>
            {update.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
            {t("composePolicy.save")}
          </Button>
          <div aria-live="polite">
            {saved && <span className="text-muted-foreground text-sm">{t("composePolicy.saved")}</span>}
            {saveError && (
              <ErrorText>
                {t("composePolicy.saveFailed")} {t(saveError.key)} {saveError.detail}
              </ErrorText>
            )}
          </div>
        </div>
      )}
    </form>
  );
}

/** The environment's allowances, for every stack; each change is saved at once with the stored policy. */
function EnvironmentAllowances({ environmentId, policy }: { environmentId: string; policy: ComposePolicy }) {
  const { t } = useTranslation();
  const update = useUpdateComposePolicyMutation(environmentId);
  const error = update.error ? describeError(update.error) : undefined;

  return (
    <div className="grid gap-3 border-t pt-4">
      <div>
        <h3 className="font-medium">{t("composePolicy.allowances")}</h3>
        <p className="text-muted-foreground text-sm">{t("composePolicy.allowancesHint")}</p>
      </div>
      <AllowanceList
        allowances={policy.allowances}
        protectedPaths={policy.protectedPaths}
        canChange={policy.canChange}
        pending={update.isPending}
        onChange={(allowances) => update.mutate({ ...requestOf(policy), allowances })}
      />
      {error && (
        <ErrorText>
          {t("allowances.failed")} {t(error.key)} {error.detail}
        </ErrorText>
      )}
    </div>
  );
}

function requestOf(policy: ComposePolicy) {
  return {
    mode: policy.mode,
    dataRoot: policy.dataRoot,
    systemReadOnlyPaths: policy.systemReadOnlyPaths,
    allowances: policy.allowances,
  };
}

function PolicyReport({ environmentId, policy }: { environmentId: string; policy: ComposePolicy }) {
  const { t } = useTranslation();
  const report = useQuery(composePolicyReportQuery(environmentId));

  return (
    <div className="grid gap-3 border-t pt-4">
      <div>
        <h3 className="font-medium">{t("composePolicy.report.title")}</h3>
        <p className="text-muted-foreground text-sm">{t("composePolicy.report.description")}</p>
      </div>
      {report.isPending ? (
        <p className="text-muted-foreground text-sm">{t("composePolicy.report.loading")}</p>
      ) : report.isError ? (
        <ErrorText>{t("composePolicy.report.loadFailed")}</ErrorText>
      ) : report.data.length === 0 ? (
        <p className="text-muted-foreground text-sm">{t("composePolicy.report.empty")}</p>
      ) : (
        <ul className="grid gap-4">
          {sortReport(report.data).map((row) => (
            <ReportRow key={row.stackId} environmentId={environmentId} policy={policy} row={row} />
          ))}
        </ul>
      )}
    </div>
  );
}

/** One stack of the report, with "Allow" next to each violation that an allowance lifts. */
function ReportRow({ environmentId, policy, row }: { environmentId: string; policy: ComposePolicy; row: StackPolicyReport }) {
  const { t } = useTranslation();
  const forStack = useSetStackPolicyAllowancesMutation(row.stackId);
  const forEnvironment = useUpdateComposePolicyMutation(environmentId);
  const pending = forStack.isPending || forEnvironment.isPending;
  const error = forStack.error ?? forEnvironment.error;
  const failure = error ? describeError(error) : undefined;

  return (
    <li className="grid gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{row.stackName}</span>
        {row.unrestricted && <Badge variant="destructive">{t("composePolicy.report.unrestricted")}</Badge>}
        {!row.checked ? (
          <Badge variant="outline">{t("composePolicy.report.notChecked")}</Badge>
        ) : row.violations.length === 0 ? (
          <Badge variant="secondary">{t("composePolicy.report.passes")}</Badge>
        ) : (
          <Badge variant={row.unrestricted ? "outline" : "destructive"}>
            {t("composePolicy.report.violations", { count: row.violations.length })}
          </Badge>
        )}
      </div>
      {row.violations.length > 0 && (
        <ul className="grid gap-2 sm:ml-4">
          {row.violations.map((violation, index) => (
            <li key={index} className="grid gap-1 text-sm">
              <span className="text-muted-foreground">
                {violation.service && <span className="font-mono">{violation.service}: </span>}
                {violation.message}
              </span>
              {violation.allowance ? (
                policy.canChange && !row.unrestricted ? (
                  <div className="flex flex-wrap items-center gap-2">
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={pending}
                      onClick={() => forStack.mutate([...row.allowances, violation.allowance!])}
                    >
                      {t("allowances.allowForStack")}
                    </Button>
                    {!violation.stackOnly && (
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={pending}
                        onClick={() =>
                          forEnvironment.mutate({ ...requestOf(policy), allowances: [...policy.allowances, violation.allowance!] })
                        }
                      >
                        {t("allowances.allowForEnvironment")}
                      </Button>
                    )}
                    <span className="text-muted-foreground font-mono text-xs">{violation.allowance}</span>
                  </div>
                ) : (
                  <span className="text-muted-foreground font-mono text-xs">
                    {t("composePolicy.report.allowance", { allowance: violation.allowance })}
                  </span>
                )
              ) : (
                <span className="text-muted-foreground text-xs">{t("composePolicy.report.noAllowance")}</span>
              )}
            </li>
          ))}
        </ul>
      )}
      {failure && (
        <ErrorText>
          {t("allowances.failed")} {t(failure.key)} {failure.detail}
        </ErrorText>
      )}
    </li>
  );
}
