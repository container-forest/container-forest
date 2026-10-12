import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { environmentsQuery } from "@/api/queries";
import { ConfirmAction } from "@/components/confirm-action";
import { Trash2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { type ApiToken, environmentNames, tokenState } from "@/features/tokens/model";
import { formatDate, formatDateTime } from "@/lib/format";

/** API tokens with their limits and a revoke button; the user's own list and an administrator's view of a user. */
export function TokenTable({
  tokens,
  revoking,
  onRevoke,
}: {
  tokens: readonly ApiToken[];
  revoking: boolean;
  onRevoke: (token: ApiToken) => void;
}) {
  const { t, i18n } = useTranslation();
  // Names only for the environments the viewer can see; an administrator sees them all.
  const environments = useQuery(environmentsQuery);
  const now = new Date();

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("tokens.columns.name")}</TableHead>
          <TableHead>{t("tokens.columns.access")}</TableHead>
          <TableHead className="hidden md:table-cell">{t("tokens.columns.expires")}</TableHead>
          <TableHead className="hidden lg:table-cell">{t("tokens.columns.lastUsed")}</TableHead>
          <TableHead className="sr-only">{t("tokens.columns.actions")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {tokens.map((token) => {
          const state = tokenState(token, now);
          const names = environmentNames(token, environments.data ?? []);
          return (
            <TableRow key={token.id}>
              <TableCell>
                <div className="grid gap-1">
                  <span className="font-medium break-all">{token.name}</span>
                  <code className="text-muted-foreground font-mono text-xs">{token.hint}…</code>
                  {/* The expiry column is hidden on narrow screens, but when a token stops working still matters there. */}
                  <div className="text-muted-foreground flex flex-wrap items-center gap-x-1 text-xs md:hidden">
                    {token.expiresAt !== null && <span>{t("tokens.columns.expires")}</span>}
                    <TokenExpiry token={token} state={state} />
                  </div>
                </div>
              </TableCell>
              <TableCell>
                <div className="flex flex-wrap gap-1">
                  <Badge variant={token.readOnly ? "outline" : "secondary"}>
                    {token.readOnly ? t("tokens.access.readOnly") : t("tokens.access.full")}
                  </Badge>
                  {token.secrets && <Badge variant="secondary">{t("tokens.access.secrets")}</Badge>}
                  {names === null ? (
                    <Badge variant="outline">{t("tokens.access.allEnvironments")}</Badge>
                  ) : names.length === 0 ? (
                    <Badge variant="destructive">{t("tokens.access.noEnvironments")}</Badge>
                  ) : (
                    names.map((name) => (
                      <Badge key={name} variant="outline">
                        {name}
                      </Badge>
                    ))
                  )}
                </div>
              </TableCell>
              <TableCell className="hidden text-sm whitespace-nowrap md:table-cell">
                <TokenExpiry token={token} state={state} />
              </TableCell>
              <TableCell className="hidden text-sm whitespace-nowrap lg:table-cell">
                {token.lastUsedAt === null ? t("tokens.neverUsed") : formatDateTime(token.lastUsedAt, i18n.resolvedLanguage)}
              </TableCell>
              <TableCell>
                <div className="flex justify-end">
                  <ConfirmAction
                    variant="outline"
                    size="sm"
                    title={t("tokens.revokeTitle", { name: token.name })}
                    description={t("tokens.revokeDescription")}
                    confirmLabel={t("tokens.revoke")}
                    disabled={revoking}
                    onConfirm={() => onRevoke(token)}
                  >
                    <Trash2Icon data-icon="inline-start" />
                    {t("tokens.revoke")}
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

/** When the token expires, marked when it already has or is about to. */
function TokenExpiry({ token, state }: { token: ApiToken; state: ReturnType<typeof tokenState> }) {
  const { t, i18n } = useTranslation();

  if (token.expiresAt === null) {
    return t("tokens.never");
  }

  return (
    <span className="flex flex-wrap items-center gap-2">
      {formatDate(token.expiresAt, i18n.resolvedLanguage)}
      {state === "expired" && <Badge variant="destructive">{t("tokens.state.expired")}</Badge>}
      {state === "expiresSoon" && <Badge variant="outline">{t("tokens.state.expiresSoon")}</Badge>}
    </span>
  );
}
