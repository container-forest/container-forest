import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { unmanagedStacksQuery } from "@/api/queries";
import { useUnmanagedStackActionMutation } from "@/api/unmanaged-stacks";
import { ConfirmAction } from "@/components/confirm-action";
import { ErrorText } from "@/components/form-messages";
import { DownloadIcon, Loader2Icon, PlayIcon, RotateCwIcon, SquareIcon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { Environment } from "@/features/containers/model";
import { type StacksSearch, describeError, filterUnmanagedStacks } from "@/features/stacks/model";
import type { MessageKey } from "@/i18n";
import {
  type UnmanagedStack,
  type UnmanagedStackAction,
  isUnmanagedActionAllowed,
  isUnmanagedActionEnabled,
  needsUnmanagedConfirmation,
  shortConfigFiles,
  unmanagedStackActions,
  unmanagedStackLink,
} from "@/features/unmanaged-stacks/model";
import { rowActionsClass, rowClass, rowLinkClass } from "@/lib/row-link";
import { cn } from "@/lib/utils";

/** The last action's outcome, with what compose printed. Keys, not text, so it follows a language switch. */
type Outcome =
  | { kind: "done"; stack: string; action: UnmanagedStackAction; output: string[] }
  | { kind: "composeFailed"; stack: string; error: string | null; output: string[] }
  | { kind: "error"; stack: string; key: MessageKey; detail?: string };

const icons = { up: PlayIcon, pull: DownloadIcon, restart: RotateCwIcon, down: SquareIcon } as const;

/**
 * Compose projects the host's administrator runs by hand (ADR-0016), under the managed stacks. A row opens the
 * stack's page; its buttons are quick compose operations on the whole project. Shown only to users with a role on
 * the whole environment, and only when its agent finds such projects.
 */
export function UnmanagedStacksSection({ environment, search }: { environment: Environment; search: StacksSearch }) {
  const { t } = useTranslation();
  const stacks = useQuery({ ...unmanagedStacksQuery(environment.id), enabled: environment.isOnline });
  const action = useUnmanagedStackActionMutation(environment.id);
  const [outcome, setOutcome] = useState<Outcome>();

  // Nothing to show: an offline agent, one without unmanaged stack roots, or a user without a role on the environment.
  // Only when the filter asks for unmanaged stacks alone does the screen say so; otherwise the section is just absent.
  if (!environment.isOnline || stacks.isPending || (stacks.isSuccess && stacks.data.length === 0)) {
    return search.kind === "unmanaged" && (!environment.isOnline || stacks.isSuccess) ? (
      <p className="text-muted-foreground">{environment.isOnline ? t("unmanagedStacks.empty") : t("unmanagedStack.agentOffline")}</p>
    ) : null;
  }

  const shown = stacks.isSuccess ? filterUnmanagedStacks(stacks.data, search) : [];

  const run = (stack: UnmanagedStack, kind: UnmanagedStackAction) => {
    setOutcome(undefined);
    action.mutate(
      { stack: stack.name, action: kind },
      {
        onSuccess: (result) =>
          setOutcome(
            result.succeeded
              ? { kind: "done", stack: stack.name, action: kind, output: [...result.output] }
              : { kind: "composeFailed", stack: stack.name, error: result.error, output: [...result.output] },
          ),
        onError: (error) => setOutcome({ kind: "error", stack: stack.name, ...describeError(error) }),
      },
    );
  };

  return (
    <section className="grid gap-3" aria-labelledby="unmanaged-stacks-title">
      <div>
        <h2 id="unmanaged-stacks-title" className="font-heading text-lg font-semibold">
          {t("unmanagedStacks.title")}
        </h2>
        <p className="text-muted-foreground text-sm">{t("unmanagedStacks.description")}</p>
      </div>

      {stacks.isError ? (
        <ErrorText>
          {t("unmanagedStacks.loadFailed")} {t(describeError(stacks.error).key)} {describeError(stacks.error).detail}
        </ErrorText>
      ) : (
        <>
          <OutcomeText outcome={outcome} />
          {shown.length === 0 ? (
            <p className="text-muted-foreground">{t("stacks.filters.noMatch")}</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("unmanagedStacks.columns.name")}</TableHead>
                  <TableHead className="hidden md:table-cell">{t("unmanagedStacks.columns.directory")}</TableHead>
                  <TableHead className="hidden sm:table-cell">{t("unmanagedStacks.columns.state")}</TableHead>
                  <TableHead className="text-right">
                    <span className="sr-only sm:not-sr-only">{t("unmanagedStacks.columns.actions")}</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {shown.map((stack) => (
                  <TableRow key={stack.name} className={rowClass}>
                    <TableCell className="whitespace-normal">
                      <Link
                        {...unmanagedStackLink(environment.id, stack.name)}
                        className={cn("font-medium whitespace-nowrap", rowLinkClass)}
                      >
                        {stack.name}
                      </Link>
                      {stack.isSystem && (
                        <Badge variant="outline" className="ml-2" title={t("unmanagedStacks.systemHint")}>
                          {t("stacks.system")}
                        </Badge>
                      )}
                      <div className="text-muted-foreground font-mono text-xs break-all md:hidden">{stack.directory}</div>
                    </TableCell>
                    <TableCell className="hidden whitespace-normal md:table-cell">
                      <div className="font-mono text-xs break-all">{stack.directory}</div>
                      <div className="text-muted-foreground font-mono text-xs">{shortConfigFiles(stack).join(", ")}</div>
                    </TableCell>
                    <TableCell className="text-muted-foreground hidden text-sm sm:table-cell">
                      {stack.hasContainers ? t("unmanagedStacks.hasContainers") : t("unmanagedStacks.noContainers")}
                    </TableCell>
                    <TableCell className={cn("text-right", rowActionsClass)}>
                      <StackActions
                        stack={stack}
                        busyAction={action.isPending && action.variables?.stack === stack.name ? action.variables.action : undefined}
                        locked={action.isPending}
                        onAction={run}
                      />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </>
      )}
    </section>
  );
}

function StackActions({
  stack,
  busyAction,
  locked,
  onAction,
}: {
  stack: UnmanagedStack;
  busyAction: UnmanagedStackAction | undefined;
  locked: boolean;
  onAction: (stack: UnmanagedStack, action: UnmanagedStackAction) => void;
}) {
  const { t } = useTranslation();
  const allowed = unmanagedStackActions.filter((kind) => isUnmanagedActionAllowed(kind, stack));
  if (allowed.length === 0) {
    return <span className="text-muted-foreground text-sm">{t("containers.viewOnly")}</span>;
  }

  return (
    <div className="flex items-center justify-end gap-1">
      {allowed.map((kind) => {
        const Icon = icons[kind];
        const label = t(`unmanagedStacks.actions.${kind}`);
        const content = busyAction === kind ? <Loader2Icon className="animate-spin" /> : <Icon />;
        const disabled = locked || !isUnmanagedActionEnabled(kind, stack);

        return needsUnmanagedConfirmation(kind) ? (
          <ConfirmAction
            key={kind}
            variant="ghost"
            size="icon"
            title={t(`unmanagedStacks.confirm.${kind as "restart" | "down"}Title`, { name: stack.name })}
            description={t(`unmanagedStacks.confirm.${kind as "restart" | "down"}Description`)}
            confirmLabel={label}
            disabled={disabled}
            label={`${label}: ${stack.name}`}
            onConfirm={() => onAction(stack, kind)}
          >
            {content}
          </ConfirmAction>
        ) : (
          <Button
            key={kind}
            variant="ghost"
            size="icon"
            title={label}
            aria-label={`${label}: ${stack.name}`}
            disabled={disabled}
            onClick={() => onAction(stack, kind)}
          >
            {content}
          </Button>
        );
      })}
    </div>
  );
}

/** The last action's outcome above the list; compose's output folds out below it. */
function OutcomeText({ outcome }: { outcome: Outcome | undefined }) {
  const { t } = useTranslation();

  return (
    <div aria-live="polite" className="grid gap-2">
      {outcome?.kind === "done" && (
        <p role="status" className="text-sm">
          {t(`unmanagedStacks.done.${outcome.action}`, { name: outcome.stack })}
        </p>
      )}
      {outcome?.kind === "composeFailed" && (
        <ErrorText>
          {t("stacks.quick.composeFailed", { name: outcome.stack })} {outcome.error}
        </ErrorText>
      )}
      {outcome?.kind === "error" && (
        <ErrorText>
          {outcome.stack}: {t(outcome.key)} {outcome.detail}
        </ErrorText>
      )}
      {outcome !== undefined && outcome.kind !== "error" && outcome.output.length > 0 && (
        <details open={outcome.kind === "composeFailed"}>
          <summary className="text-muted-foreground cursor-pointer text-sm">{t("unmanagedStacks.output")}</summary>
          <pre className="bg-muted mt-2 max-h-80 overflow-auto rounded-md p-3 font-mono text-xs whitespace-pre-wrap">
            {outcome.output.join("\n")}
          </pre>
        </details>
      )}
    </div>
  );
}
