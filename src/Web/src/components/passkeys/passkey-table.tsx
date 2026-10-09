import { useTranslation } from "react-i18next";
import { ConfirmAction } from "@/components/confirm-action";
import { Trash2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { Passkey } from "@/features/passkeys/model";
import { formatDate } from "@/lib/format";

/** Passkeys with a remove button; an extra action per row (renaming) for the user's own list. */
export function PasskeyTable({
  passkeys,
  removing,
  onRemove,
  extraAction,
}: {
  passkeys: readonly Passkey[];
  removing: boolean;
  onRemove: (passkey: Passkey) => void;
  extraAction?: (passkey: Passkey) => React.ReactNode;
}) {
  const { t, i18n } = useTranslation();

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("passkeys.columns.name")}</TableHead>
          <TableHead className="hidden sm:table-cell">{t("passkeys.columns.created")}</TableHead>
          <TableHead className="sr-only">{t("passkeys.columns.actions")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {passkeys.map((passkey) => (
          <TableRow key={passkey.id}>
            <TableCell>
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium break-all">{passkey.name}</span>
                {passkey.isBackedUp && <Badge variant="outline">{t("passkeys.synced")}</Badge>}
              </div>
            </TableCell>
            <TableCell className="hidden text-sm whitespace-nowrap sm:table-cell">{formatDate(passkey.createdAt, i18n.resolvedLanguage)}</TableCell>
            <TableCell>
              <div className="flex justify-end gap-2">
                {extraAction?.(passkey)}
                <ConfirmAction
                  variant="outline"
                  size="sm"
                  title={t("passkeys.removeTitle", { name: passkey.name })}
                  description={t("passkeys.removeDescription")}
                  confirmLabel={t("passkeys.remove")}
                  disabled={removing}
                  onConfirm={() => onRemove(passkey)}
                >
                  <Trash2Icon data-icon="inline-start" />
                  {t("passkeys.remove")}
                </ConfirmAction>
              </div>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
