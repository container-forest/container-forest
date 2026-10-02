import { useTranslation } from "react-i18next";
import { Loader2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { type DeploymentStatus, isFinished, statusVariant } from "@/features/stacks/model";

export function DeploymentStatusBadge({ status }: { status: DeploymentStatus }) {
  const { t } = useTranslation();

  return (
    <Badge variant={statusVariant(status)}>
      {!isFinished(status) && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
      {t(`deployments.status.${status}`)}
    </Badge>
  );
}
