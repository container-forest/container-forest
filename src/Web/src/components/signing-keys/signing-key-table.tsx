import { useQuery } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { environmentsQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { Trash2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { type SigningKey, scopeNames } from "@/features/signing-keys/model";
import { formatDateTime } from "@/lib/format";

/** Signing keys with a remove button; an extra action per row (editing) for the user's own list. */
export function SigningKeyTable({
  keys,
  removing,
  onRemove,
  extraAction,
}: {
  keys: readonly SigningKey[];
  removing: boolean;
  onRemove: (key: SigningKey) => void;
  extraAction?: (key: SigningKey) => ReactNode;
}) {
  const { t, i18n } = useTranslation();
  const environments = useQuery(environmentsQuery);

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("signingKeys.columns.name")}</TableHead>
          <TableHead className="hidden md:table-cell">{t("signingKeys.columns.environments")}</TableHead>
          <TableHead className="hidden lg:table-cell">{t("signingKeys.columns.lastUsed")}</TableHead>
          <TableHead className="sr-only">{t("signingKeys.columns.actions")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {keys.map((key) => {
          const scope = scopeNames(key, environments.data ?? []);
          return (
            <TableRow key={key.id}>
              <TableCell className="whitespace-normal">
                <div className="grid min-w-0 gap-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium break-all">{key.name}</span>
                    {key.hardwareBacked && <Badge variant="outline">{t("signingKeys.hardware")}</Badge>}
                    {key.addedBy && <Badge variant="secondary">{t("signingKeys.addedBy", { name: key.addedBy })}</Badge>}
                  </div>
                  <code className="text-muted-foreground font-mono text-xs break-all" title={t("signingKeys.columns.fingerprint")}>
                    {key.fingerprint}
                  </code>
                </div>
              </TableCell>
              <TableCell className="hidden text-sm whitespace-normal md:table-cell">
                {scope === null ? t("signingKeys.allEnvironments") : scope.join(", ") || "—"}
              </TableCell>
              <TableCell className="hidden text-sm whitespace-nowrap lg:table-cell">
                {key.lastUsedAt ? formatDateTime(key.lastUsedAt, i18n.resolvedLanguage) : t("signingKeys.neverUsed")}
              </TableCell>
              <TableCell>
                <div className="flex justify-end gap-2">
                  {extraAction?.(key)}
                  <ConfirmAction
                    variant="outline"
                    size="sm"
                    title={t("signingKeys.removeTitle", { name: key.name })}
                    description={t("signingKeys.removeDescription")}
                    confirmLabel={t("signingKeys.remove")}
                    disabled={removing}
                    onConfirm={() => onRemove(key)}
                  >
                    <Trash2Icon data-icon="inline-start" />
                    {t("signingKeys.remove")}
                  </ConfirmAction>
                </div>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}
