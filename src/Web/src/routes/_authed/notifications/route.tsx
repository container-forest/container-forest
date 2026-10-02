import { Link, Outlet, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

export const Route = createFileRoute("/_authed/notifications")({
  component: NotificationsLayout,
});

/** Channels (with their rules) and the history are two views of one feature: tabs above both. */
function NotificationsLayout() {
  const { t } = useTranslation();

  return (
    <div className="grid gap-6">
      <nav aria-label={t("notifications.tabs.label")} className="flex gap-1 border-b">
        <NotificationsTab to="/notifications/channels">{t("notifications.tabs.channels")}</NotificationsTab>
        <NotificationsTab to="/notifications/history">{t("notifications.tabs.history")}</NotificationsTab>
      </nav>
      <Outlet />
    </div>
  );
}

function NotificationsTab({ to, children }: { to: "/notifications/channels" | "/notifications/history"; children: string }) {
  return (
    <Link
      to={to}
      className="-mb-px border-b-2 px-3 py-2 text-sm"
      inactiveProps={{ className: "text-muted-foreground hover:text-foreground border-transparent" }}
      activeProps={{ className: "text-foreground border-primary font-medium" }}
    >
      {children}
    </Link>
  );
}
