import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { containersQuery } from "@/api/queries";
import { useDeployStackMutation, useStackActionMutation } from "@/api/stacks";
import { ConfirmAction } from "@/components/confirm-action";
import { Loader2Icon, PlayIcon, RotateCwIcon, SquareIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import type { MessageKey } from "@/i18n";
import { type Stack, type StackAction, describeError } from "@/features/stacks/model";

/** What a quick action ended with, for the line above the list. Keys, not text, so it follows a language switch. */
export type StackQuickResult =
  | { kind: "deployed"; stack: string; deploymentId: string }
  | { kind: "done"; stack: string; action: StackAction }
  | { kind: "composeFailed"; stack: string; error: string | null }
  | { kind: "error"; stack: string; key: MessageKey; detail?: string };

/**
 * The stack's actions as icon buttons, in the order of the containers' (start, stop, restart): deploy, take down
 * and restart. Take down and restart ask first. Shown only where the user may do them; the aggregator decides anyway.
 */
export function StackQuickActions({ stack, onResult }: { stack: Stack; onResult: (result: StackQuickResult | undefined) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const deploy = useDeployStackMutation(stack.id);
  const action = useStackActionMutation(stack.id);
  const busy = deploy.isPending || action.isPending;
  // Containers of the stack change with every action; their list may be open elsewhere.
  const refreshContainers = () => queryClient.invalidateQueries({ queryKey: containersQuery(stack.environmentId).queryKey });

  const fail = (error: unknown) => onResult({ kind: "error", stack: stack.name, ...describeError(error) });

  const onDeploy = () => {
    onResult(undefined);
    deploy.mutate(false, {
      onSuccess: (deployment) => onResult({ kind: "deployed", stack: stack.name, deploymentId: deployment.id }),
      onError: fail,
    });
  };

  const onAction = (kind: StackAction) => {
    onResult(undefined);
    action.mutate(
      { action: kind },
      {
        onSuccess: (result) =>
          onResult(
            result.succeeded
              ? { kind: "done", stack: stack.name, action: kind }
              : { kind: "composeFailed", stack: stack.name, error: result.error },
          ),
        onError: fail,
        onSettled: refreshContainers,
      },
    );
  };

  // System stacks are updated through the helper container, not from here; a stack never deployed has nothing to stop.
  const deployed = stack.deployedCommit !== null;
  const spinner = <Loader2Icon className="animate-spin" />;

  if (stack.isSystem || (!stack.canDeploy && !stack.canRestart)) {
    return <span className="text-muted-foreground text-sm">{t("containers.viewOnly")}</span>;
  }

  return (
    <div className="flex items-center justify-end gap-1">
      {stack.canDeploy && (
        <Button
          variant="ghost"
          size="icon"
          title={t("stack.actions.deploy")}
          aria-label={`${t("stack.actions.deploy")}: ${stack.name}`}
          disabled={busy}
          onClick={onDeploy}
        >
          {deploy.isPending ? spinner : <PlayIcon />}
        </Button>
      )}
      {stack.canDeploy && (
        <ConfirmAction
          variant="ghost"
          size="icon"
          title={t("stack.actions.confirmDown.title", { name: stack.name })}
          description={t("stack.actions.confirmDown.description")}
          confirmLabel={t("stack.actions.down")}
          disabled={busy || !deployed}
          label={`${t("stack.actions.down")}: ${stack.name}`}
          onConfirm={() => onAction("down")}
        >
          {action.isPending && action.variables?.action === "down" ? spinner : <SquareIcon />}
        </ConfirmAction>
      )}
      {stack.canRestart && (
        <ConfirmAction
          variant="ghost"
          size="icon"
          title={t("stack.actions.confirmRestart.title", { name: stack.name })}
          description={t("stack.actions.confirmRestart.description")}
          confirmLabel={t("stack.actions.restart")}
          disabled={busy || !deployed}
          label={`${t("stack.actions.restart")}: ${stack.name}`}
          onConfirm={() => onAction("restart")}
        >
          {action.isPending && action.variables?.action === "restart" ? spinner : <RotateCwIcon />}
        </ConfirmAction>
      )}
    </div>
  );
}
