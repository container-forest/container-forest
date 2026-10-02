import { useTranslation } from "react-i18next";
import type { User } from "@/api/users";
import { Badge } from "@/components/ui/badge";
import { type UserStatus, userStatus } from "@/lib/users";

const statusVariants = {
  active: "secondary",
  locked: "outline",
  deactivated: "destructive",
} as const satisfies Record<UserStatus, "secondary" | "outline" | "destructive">;

export function UserStatusBadge({ user, now }: { user: User; now: Date }) {
  const { t } = useTranslation();
  const status = userStatus(user, now);

  return <Badge variant={statusVariants[status]}>{t(`users.status.${status}`)}</Badge>;
}

export function UserKindBadge({ user }: { user: User }) {
  const { t } = useTranslation();

  return user.isAdmin ? <Badge>{t("users.kind.admin")}</Badge> : <Badge variant="outline">{t("users.kind.user")}</Badge>;
}
