import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { containersQuery, environmentsQuery } from "@/api/queries";
import { ContainerTable } from "@/components/containers/container-table";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon } from "@/components/icons";
import { useLiveStatus } from "@/components/live/hub-provider";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { pollingInterval } from "@/features/live/model";

/** How often the containers are reloaded without live updates, and with them (agents too old to send events). */
const CONTAINERS_REFRESH_MS = 15_000;
const CONTAINERS_REFRESH_WHILE_LIVE_MS = 60_000;

/**
 * A stack's containers as its environment's agent reports them, with the same quick actions and links as on the
 * containers screen. Compose labels say which stack a container belongs to, for managed and unmanaged stacks alike.
 */
export function StackContainersCard({
  environmentId,
  stackName,
  emptyText,
}: {
  environmentId: string;
  stackName: string;
  /** Why a stack of this kind has no containers. */
  emptyText: string;
}) {
  const { t } = useTranslation();
  const live = useLiveStatus();
  const environments = useQuery(environmentsQuery);
  const online = environments.data?.find((e) => e.id === environmentId)?.isOnline ?? false;
  const containers = useQuery({
    ...containersQuery(environmentId),
    // An offline agent answers 503 every time; there is nothing to load until it reconnects.
    enabled: online,
    refetchInterval: pollingInterval(live, CONTAINERS_REFRESH_MS, CONTAINERS_REFRESH_WHILE_LIVE_MS),
  });
  const own = (containers.data ?? []).filter((c) => c.stackName === stackName);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("stack.containers.title")}</CardTitle>
        <CardDescription>{t("stack.containers.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        {!online ? (
          <p role="status" className="text-muted-foreground text-sm">
            {t("stack.containers.agentOffline")}
          </p>
        ) : containers.isPending ? (
          <p className="text-muted-foreground flex items-center gap-2 text-sm">
            <Loader2Icon className="animate-spin" />
            {t("stack.containers.loading")}
          </p>
        ) : containers.isError && own.length === 0 ? (
          <ErrorText>{t("stack.containers.loadFailed")}</ErrorText>
        ) : own.length === 0 ? (
          <p className="text-muted-foreground text-sm">{emptyText}</p>
        ) : (
          <ContainerTable environmentId={environmentId} containers={own} showStack={false} />
        )}
      </CardContent>
    </Card>
  );
}
