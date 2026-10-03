import { HttpError, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { type ReactNode, createContext, useContext, useEffect, useState } from "react";
import { type LiveEvent, type LiveStatus, invalidationsFor, retryDelay } from "@/features/live/model";

const LiveStatusContext = createContext<LiveStatus>("connecting");

/** The state of the panel's connection to /hubs/live; "connecting" outside a HubProvider. */
export function useLiveStatus(): LiveStatus {
  return useContext(LiveStatusContext);
}

/**
 * The application's one connection to /hubs/live, mounted inside the signed-in layout. Events only invalidate
 * TanStack Query queries; nothing is stored apart from the cache. After a lost connection (the aggregator restarted,
 * for instance after an update) everything is reloaded, since events may have been missed meanwhile.
 */
export function HubProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<LiveStatus>("connecting");

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/live")
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: ({ previousRetryCount }) => retryDelay(previousRetryCount),
      })
      .configureLogging(LogLevel.Warning)
      .build();
    let stopped = false;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const handle = (event: LiveEvent) => {
      for (const { queryKey, exact } of invalidationsFor(event)) {
        void queryClient.invalidateQueries({ queryKey, exact });
      }
    };

    connection.on("EnvironmentsChanged", () => {
      handle({ name: "EnvironmentsChanged" });
      // Roles may have changed which environments this user sees, and so which updates reach the connection.
      void connection.invoke("Resubscribe").catch(() => undefined);
    });
    connection.on("ContainersChanged", (environmentId: string) => handle({ name: "ContainersChanged", environmentId }));
    connection.on("StackChanged", (environmentId: string, stackId: string) =>
      handle({ name: "StackChanged", environmentId, stackId }),
    );
    connection.on("DeploymentChanged", (environmentId: string, stackId: string, deploymentId: string) =>
      handle({ name: "DeploymentChanged", environmentId, stackId, deploymentId }),
    );

    connection.onreconnecting(() => setStatus("reconnecting"));
    connection.onreconnected(() => {
      setStatus("live");
      void queryClient.invalidateQueries();
    });
    connection.onclose(() => {
      if (!stopped) {
        setStatus("reconnecting");
        retry = setTimeout(() => void start(1), retryDelay(1));
      }
    });

    // The first start is not covered by automatic reconnection: retried here until it works.
    const start = async (attempt: number) => {
      try {
        await connection.start();
        if (stopped) {
          await connection.stop();
          return;
        }

        setStatus("live");
        if (attempt > 0) {
          void queryClient.invalidateQueries();
        }
      } catch (error) {
        if (stopped) {
          return;
        }

        // 401/403: the session ended or the user was deactivated; retrying would not help.
        if (error instanceof HttpError && (error.statusCode === 401 || error.statusCode === 403)) {
          setStatus("offline");
          return;
        }

        setStatus(attempt === 0 ? "connecting" : "reconnecting");
        retry = setTimeout(() => void start(attempt + 1), retryDelay(attempt));
      }
    };

    void start(0);
    return () => {
      stopped = true;
      clearTimeout(retry);
      void connection.stop();
    };
  }, [queryClient]);

  return <LiveStatusContext.Provider value={status}>{children}</LiveStatusContext.Provider>;
}
