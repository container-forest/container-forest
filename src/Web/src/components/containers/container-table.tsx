import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useContainerActionMutation } from "@/api/containers";
import { Loader2Icon, PlayIcon, RotateCwIcon, SquareIcon, Trash2Icon } from "@/components/icons";
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { MessageKey } from "@/i18n";
import {
  type Container,
  type ContainerAction,
  DEFAULT_STOP_TIMEOUT,
  MAX_STOP_TIMEOUT,
  describeError,
  isActionAvailable,
  needsConfirmation,
  parseStopTimeout,
  stateFilterOf,
  takesStopTimeout,
} from "@/features/containers/model";
import { rowActionsClass, rowClass, rowLinkClass } from "@/lib/row-link";
import { cn } from "@/lib/utils";

/** The outcome of the last action. Kept as a key, not text, so it follows a language switch. */
interface Notice {
  kind: "success" | "error";
  key: MessageKey;
  name?: string;
  detail?: string;
}

interface PendingAction {
  container: Container;
  action: "stop" | "restart" | "remove";
}

/**
 * Containers with their quick actions: start at once, stop and restart after a confirmation that asks for the stop
 * timeout. A whole row opens the container (its log); the action buttons sit above that link. The outcome of the
 * last action shows above the table. Used by the containers screen and by the stack page.
 */
export function ContainerTable({
  environmentId,
  containers,
  showStack = true,
  from,
}: {
  environmentId: string;
  containers: Container[];
  /** The stack page shows only its own containers; the column would repeat its name. */
  showStack?: boolean;
  /** The `from` of the container links (`originParam`), so the container's back link returns to this page. */
  from?: string;
}) {
  const { t, i18n } = useTranslation();
  const action = useContainerActionMutation(environmentId);
  const [pending, setPending] = useState<PendingAction>();
  const [notice, setNotice] = useState<Notice>();

  const noticeText = ({ key, name, detail }: Notice) => {
    const text = t(key, { name });
    return detail ? `${text} ${detail}` : text;
  };

  const run = (container: Container, kind: ContainerAction, timeoutSeconds?: number) => {
    setNotice(undefined);
    action.mutate(
      { containerId: container.id, action: kind, timeoutSeconds },
      {
        onSuccess: () => setNotice({ kind: "success", key: `containers.done.${kind}`, name: container.name }),
        onError: (error) => setNotice({ kind: "error", ...describeError(error) }),
      },
    );
  };

  const onAction = (container: Container, kind: ContainerAction) => {
    if (needsConfirmation(kind)) {
      setPending({ container, action: kind });
    } else {
      run(container, kind);
    }
  };

  return (
    <div className="grid gap-3">
      <div aria-live="polite">
        {notice && (
          <p role={notice.kind === "error" ? "alert" : "status"} className={notice.kind === "error" ? "text-destructive text-sm" : "text-sm"}>
            {noticeText(notice)}
          </p>
        )}
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("containers.columns.name")}</TableHead>
            {showStack && <TableHead className="hidden md:table-cell">{t("containers.columns.stack")}</TableHead>}
            <TableHead>{t("containers.columns.state")}</TableHead>
            <TableHead className="hidden lg:table-cell">{t("containers.columns.image")}</TableHead>
            <TableHead className="hidden xl:table-cell">{t("containers.columns.created")}</TableHead>
            <TableHead className="text-right">
              <span className="sr-only sm:not-sr-only">{t("containers.columns.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {containers.map((container) => (
            <ContainerRow
              key={container.id}
              environmentId={environmentId}
              container={container}
              showStack={showStack}
              from={from}
              busy={action.isPending && action.variables?.containerId === container.id}
              locked={action.isPending}
              language={i18n.resolvedLanguage}
              onAction={onAction}
            />
          ))}
        </TableBody>
      </Table>

      <StopDialog
        pending={pending}
        onCancel={() => setPending(undefined)}
        onConfirm={(timeoutSeconds) => {
          if (pending) {
            run(pending.container, pending.action, timeoutSeconds);
          }

          setPending(undefined);
        }}
      />
    </div>
  );
}

const actionIcons = { start: PlayIcon, stop: SquareIcon, restart: RotateCwIcon, remove: Trash2Icon } as const;

function ContainerRow({
  environmentId,
  container,
  showStack,
  from,
  busy,
  locked,
  language,
  onAction,
}: {
  environmentId: string;
  container: Container;
  showStack: boolean;
  from: string | undefined;
  busy: boolean;
  locked: boolean;
  language: string | undefined;
  onAction: (container: Container, action: ContainerAction) => void;
}) {
  const { t } = useTranslation();
  const group = stateFilterOf(container.state);

  return (
    <TableRow className={rowClass}>
      {/* Compose names can be long (project-service-index); they wrap at the hyphens instead of widening the table. */}
      <TableCell className="max-w-72 whitespace-normal">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 font-medium">
          {/* Everyone who sees a container may read its log (Viewer and up). The name, not the id, keeps the link
              working after compose recreates the container. The link covers the whole row. */}
          <Link
            to="/containers/$environmentId/$containerId/logs"
            params={{ environmentId, containerId: container.name }}
            search={{ from }}
            className={rowLinkClass}
          >
            {container.name}
          </Link>
          {container.isSystem && (
            <Badge variant="outline" title={t("containers.systemHint")}>
              {t("containers.system")}
            </Badge>
          )}
        </div>
        <div className="text-muted-foreground font-mono text-xs">
          {container.id.slice(0, 12)}
          {/* Narrow screens have no stack column; the service still says what the container is. */}
          {showStack && container.stackName && <span className="md:hidden"> · {container.stackName}</span>}
        </div>
      </TableCell>
      {showStack && (
        <TableCell className="hidden md:table-cell">
          {container.stackName ?? <span className="text-muted-foreground">{t("containers.filters.noStack")}</span>}
          {container.serviceName && <div className="text-muted-foreground text-xs">{container.serviceName}</div>}
        </TableCell>
      )}
      <TableCell>
        <Badge variant={group === "running" ? "default" : container.state === "Dead" ? "destructive" : "secondary"}>
          {t(`containers.states.${container.state}`)}
        </Badge>
        <div className="text-muted-foreground mt-1 hidden text-xs sm:block">{container.statusText}</div>
      </TableCell>
      <TableCell className="hidden max-w-52 truncate font-mono text-xs lg:table-cell" title={container.image}>
        {container.image}
      </TableCell>
      <TableCell className="hidden text-sm whitespace-nowrap xl:table-cell">
        {container.createdAt &&
          new Intl.DateTimeFormat(language, { dateStyle: "short", timeStyle: "short" }).format(new Date(container.createdAt))}
      </TableCell>
      <TableCell className={cn("text-right", rowActionsClass)}>
        {container.canControl ? (
          <div className="flex items-center justify-end gap-1">
            {(container.canRemove ? (["start", "stop", "restart", "remove"] as const) : (["start", "stop", "restart"] as const)).map((kind) => {
              const Icon = actionIcons[kind];
              const label = t(`containers.actions.${kind}`);
              return (
                <Button
                  key={kind}
                  variant="ghost"
                  size="icon"
                  aria-label={`${label}: ${container.name}`}
                  title={label}
                  disabled={locked || !isActionAvailable(kind, container)}
                  onClick={() => onAction(container, kind)}
                >
                  {busy ? <Loader2Icon className="animate-spin" /> : <Icon />}
                </Button>
              );
            })}
          </div>
        ) : (
          <span className="text-muted-foreground text-sm">{t("containers.viewOnly")}</span>
        )}
      </TableCell>
    </TableRow>
  );
}

/** Confirms a stop, restart or removal; stop and restart also ask how long Docker may wait for a clean exit. */
function StopDialog({
  pending,
  onCancel,
  onConfirm,
}: {
  pending: PendingAction | undefined;
  onCancel: () => void;
  onConfirm: (timeoutSeconds: number | undefined) => void;
}) {
  const { t } = useTranslation();
  const [timeout, setTimeoutText] = useState(String(DEFAULT_STOP_TIMEOUT));
  const withTimeout = pending !== undefined && takesStopTimeout(pending.action);
  const seconds = withTimeout ? parseStopTimeout(timeout) : 0;

  return (
    <AlertDialog
      open={pending !== undefined}
      onOpenChange={(open) => {
        if (!open) {
          setTimeoutText(String(DEFAULT_STOP_TIMEOUT));
          onCancel();
        }
      }}
    >
      {pending && (
        <AlertDialogContent>
          <form
            className="grid gap-4"
            onSubmit={(event) => {
              event.preventDefault();
              if (seconds !== null) {
                setTimeoutText(String(DEFAULT_STOP_TIMEOUT));
                onConfirm(withTimeout ? seconds : undefined);
              }
            }}
          >
            <AlertDialogHeader>
              <AlertDialogTitle>{t(`containers.confirm.${pending.action}Title`, { name: pending.container.name })}</AlertDialogTitle>
              <AlertDialogDescription>{t(`containers.confirm.${pending.action}Description`)}</AlertDialogDescription>
            </AlertDialogHeader>
            {withTimeout && (
            <div className="grid gap-1.5">
              <Label htmlFor="stop-timeout">{t("containers.confirm.timeout")}</Label>
              <Input
                id="stop-timeout"
                inputMode="numeric"
                className="w-28"
                value={timeout}
                aria-invalid={seconds === null}
                aria-describedby="stop-timeout-hint"
                onChange={(event) => setTimeoutText(event.target.value)}
              />
              <p id="stop-timeout-hint" className={seconds === null ? "text-destructive text-sm" : "text-muted-foreground text-sm"}>
                {seconds === null
                  ? t("containers.confirm.timeoutInvalid", { max: MAX_STOP_TIMEOUT })
                  : t("containers.confirm.timeoutHint", { max: MAX_STOP_TIMEOUT })}
              </p>
            </div>
            )}
            <AlertDialogFooter>
              <AlertDialogCancel type="button">{t("containers.confirm.cancel")}</AlertDialogCancel>
              <Button type="submit" variant={pending.action === "restart" ? "default" : "destructive"} disabled={seconds === null}>
                {t(`containers.actions.${pending.action}`)}
              </Button>
            </AlertDialogFooter>
          </form>
        </AlertDialogContent>
      )}
    </AlertDialog>
  );
}

