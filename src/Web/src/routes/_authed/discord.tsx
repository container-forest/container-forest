import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { createFileRoute } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { type DiscordLinkCode, useIssueDiscordLinkCodeMutation, useUnlinkDiscordMutation } from "@/api/discord";
import { discordLinkQuery, signInMethodsQuery } from "@/api/queries";
import { CheckIcon, CopyIcon, LinkIcon, Loader2Icon, MessageCircleIcon, RefreshCwIcon, UnlinkIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { formatMinutesSeconds, useSecondsLeft } from "@/hooks/use-seconds-left";
import { z } from "zod";

/** How often the status is reloaded while a code waits for /link, so the screen notices the bot redeeming it. */
const LINK_POLL_INTERVAL_MS = 3000;

// The aggregator comes back here after linking through Discord's OAuth2 (ADR-0009).
const searchSchema = z.object({
  linked: z.literal("discord").optional().catch(undefined),
  error: z.literal("discord").optional().catch(undefined),
});

export const Route = createFileRoute("/_authed/discord")({
  validateSearch: searchSchema,
  loader: ({ context }) => context.queryClient.ensureQueryData(discordLinkQuery),
  component: DiscordScreen,
  errorComponent: DiscordLoadError,
});

/** Linking the user's Discord account to the bot: the current link, and a one-time code for /link. */
function DiscordScreen() {
  const { t } = useTranslation();
  const [code, setCode] = useState<DiscordLinkCode>();
  const secondsLeft = useSecondsLeft(code?.expiresAt);
  const waitingForBot = code !== undefined && secondsLeft !== 0;
  const { data: link } = useSuspenseQuery({
    ...discordLinkQuery,
    refetchInterval: waitingForBot ? LINK_POLL_INTERVAL_MS : false,
  });

  // The link time changes only when the bot redeems a code; the code shown is then used up.
  const [linkedAtWhenIssued, setLinkedAtWhenIssued] = useState<string | null>(null);
  if (code !== undefined && link.linkedAt !== linkedAtWhenIssued) {
    setCode(undefined);
  }

  return (
    <div className="grid max-w-2xl gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">{t("discord.title")}</h1>
        <p className="text-muted-foreground mt-1">{t("discord.description")}</p>
      </div>

      <LinkStatusCard linked={link.linked} discordUserName={link.discordUserName} linkedAt={link.linkedAt} />

      <OAuthLinkCard relink={link.linked} />

      <LinkCodeCard
        relink={link.linked}
        code={code}
        secondsLeft={secondsLeft}
        onIssued={(issued) => {
          setLinkedAtWhenIssued(link.linkedAt);
          setCode(issued);
        }}
      />
    </div>
  );
}

/** Linking through Discord's own sign-in: one click, no code; shown when the aggregator has Discord sign-in set up. */
function OAuthLinkCard({ relink }: { relink: boolean }) {
  const { t } = useTranslation();
  const { linked, error } = Route.useSearch();
  const methods = useQuery(signInMethodsQuery);
  if (methods.data?.discord !== true) {
    return null;
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("discord.oauth.title")}</CardTitle>
        <CardDescription>{t("discord.oauth.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {linked === "discord" && <p role="status" className="text-sm">{t("discord.oauth.done")}</p>}
        {error === "discord" && (
          <p role="alert" className="text-destructive text-sm">
            {t("discord.oauth.failed")}
          </p>
        )}
        <div>
          {/* A browser navigation to Discord's page; the aggregator brings the browser back here. */}
          <Button asChild>
            <a href="/api/me/discord/oauth">
              <MessageCircleIcon data-icon="inline-start" />
              {relink ? t("discord.oauth.relink") : t("discord.oauth.link")}
            </a>
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

function LinkStatusCard({
  linked,
  discordUserName,
  linkedAt,
}: {
  linked: boolean;
  discordUserName: string | null;
  linkedAt: string | null;
}) {
  const { t, i18n } = useTranslation();
  const unlink = useUnlinkDiscordMutation();
  const [confirming, setConfirming] = useState(false);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("discord.status.title")}</CardTitle>
        <CardDescription>
          {linked ? (
            <>
              {t("discord.status.linked", { name: discordUserName ?? "?" })}
              {linkedAt && (
                <>
                  {" "}
                  {t("discord.status.linkedSince", {
                    date: new Intl.DateTimeFormat(i18n.resolvedLanguage, { dateStyle: "long", timeStyle: "short" }).format(
                      new Date(linkedAt),
                    ),
                  })}
                </>
              )}
            </>
          ) : (
            t("discord.status.notLinked")
          )}
        </CardDescription>
      </CardHeader>

      {linked && (
        <CardContent className="grid gap-3">
          {confirming ? (
            <div role="alertdialog" aria-labelledby="unlink-confirm" className="grid gap-3">
              <p id="unlink-confirm" className="text-sm">
                {t("discord.unlink.confirm")}
              </p>
              <div className="flex gap-2">
                <Button
                  variant="destructive"
                  disabled={unlink.isPending}
                  onClick={() => unlink.mutate(undefined, { onSuccess: () => setConfirming(false) })}
                >
                  {unlink.isPending && <Loader2Icon data-icon="inline-start" className="animate-spin" />}
                  {t("discord.unlink.yes")}
                </Button>
                <Button variant="outline" autoFocus disabled={unlink.isPending} onClick={() => setConfirming(false)}>
                  {t("discord.unlink.cancel")}
                </Button>
              </div>
            </div>
          ) : (
            <div>
              <Button variant="outline" onClick={() => setConfirming(true)}>
                <UnlinkIcon data-icon="inline-start" />
                {t("discord.unlink.button")}
              </Button>
            </div>
          )}
          {unlink.isError && (
            <p role="alert" className="text-destructive text-sm">
              {t("discord.unlink.failed")}
            </p>
          )}
        </CardContent>
      )}
    </Card>
  );
}

function LinkCodeCard({
  relink,
  code,
  secondsLeft,
  onIssued,
}: {
  relink: boolean;
  code: DiscordLinkCode | undefined;
  secondsLeft: number | null;
  onIssued: (code: DiscordLinkCode) => void;
}) {
  const { t } = useTranslation();
  const issue = useIssueDiscordLinkCodeMutation();
  const expired = secondsLeft === 0;

  const getCode = () => issue.mutate(undefined, { onSuccess: onIssued });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{relink ? t("discord.code.titleRelink") : t("discord.code.title")}</CardTitle>
        <CardDescription>{t("discord.code.instructions")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {code && !expired && <LinkCommand code={code.code} secondsLeft={secondsLeft ?? 0} />}
        {code && expired && <p className="text-muted-foreground text-sm">{t("discord.code.expired")}</p>}

        <div>
          <Button variant={code && !expired ? "outline" : "default"} disabled={issue.isPending} onClick={getCode}>
            {issue.isPending ? (
              <Loader2Icon data-icon="inline-start" className="animate-spin" />
            ) : code ? (
              <RefreshCwIcon data-icon="inline-start" />
            ) : (
              <LinkIcon data-icon="inline-start" />
            )}
            {code ? t("discord.code.getNew") : t("discord.code.get")}
          </Button>
        </div>

        {issue.isError && (
          <p role="alert" className="text-destructive text-sm">
            {t("discord.code.failed")}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

/** The /link command with the code, a copy button and the time left. */
function LinkCommand({ code, secondsLeft }: { code: string; secondsLeft: number }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const command = `/link ${code}`;

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard access can be denied; the command stays selectable on screen.
    }
  };

  return (
    <div className="grid gap-2">
      <span className="text-muted-foreground text-sm">{t("discord.code.command")}</span>
      <div className="flex items-center gap-2">
        <code className="bg-muted rounded-md px-3 py-2 font-mono text-lg tracking-wider select-all">{command}</code>
        <Button variant="ghost" size="icon" aria-label={t("discord.code.copy")} title={t("discord.code.copy")} onClick={() => void copy()}>
          {copied ? <CheckIcon /> : <CopyIcon />}
        </Button>
        <span aria-live="polite" className="sr-only">
          {copied ? t("discord.code.copied") : ""}
        </span>
      </div>
      <p className="text-muted-foreground text-sm">
        {t("discord.code.expiresIn", { time: formatMinutesSeconds(secondsLeft) })} {t("discord.code.waiting")}
      </p>
    </div>
  );
}

function DiscordLoadError() {
  const { t } = useTranslation();

  return (
    <p role="alert" className="text-destructive">
      {t("discord.status.loadFailed")}
    </p>
  );
}
