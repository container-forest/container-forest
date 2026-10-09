import { useSuspenseQuery } from "@tanstack/react-query";
import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { apiTokensQuery } from "@/api/queries";
import { useRevokeApiTokenMutation } from "@/api/tokens";
import { ErrorText } from "@/components/form-messages";
import { CreateTokenDialog } from "@/components/tokens/create-token-dialog";
import { TokenTable } from "@/components/tokens/token-table";
import { describeError } from "@/features/stacks/model";

export const Route = createFileRoute("/_authed/tokens")({
  loader: ({ context }) => context.queryClient.ensureQueryData(apiTokensQuery),
  component: TokensScreen,
  errorComponent: TokensLoadError,
});

/** The signed-in user's API tokens for the CLI and scripts: create, see the limits and last use, revoke. */
function TokensScreen() {
  const { t } = useTranslation();
  const { data: tokens } = useSuspenseQuery(apiTokensQuery);
  const revoke = useRevokeApiTokenMutation();
  const revokeError = revoke.error ? describeError(revoke.error) : undefined;

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="max-w-2xl">
          <h1 className="font-heading text-2xl font-semibold">{t("tokens.title")}</h1>
          <p className="text-muted-foreground mt-1">{t("tokens.description")}</p>
        </div>
        <CreateTokenDialog />
      </div>

      {tokens.length === 0 ? (
        <p className="text-muted-foreground">{t("tokens.empty")}</p>
      ) : (
        <TokenTable tokens={tokens} revoking={revoke.isPending} onRevoke={(token) => revoke.mutate(token.id)} />
      )}
      <div aria-live="polite">
        {revokeError && (
          <ErrorText>
            {t("tokens.revokeFailed")} {t(revokeError.key)} {revokeError.detail}
          </ErrorText>
        )}
      </div>
    </div>
  );
}

function TokensLoadError() {
  const { t } = useTranslation();

  return <ErrorText>{t("tokens.loadFailed")}</ErrorText>;
}
