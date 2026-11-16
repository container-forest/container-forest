import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { stackQuery } from "@/api/queries";
import { ArrowLeftIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { type BackLinkTarget, originLink, parseOrigin } from "@/features/navigation/back";

export interface Back {
  link: BackLinkTarget;
  label: string;
}

/** The back link at the top of a detail page: muted text, or a ghost button on the container and environment pages. */
export function BackLink({ link, label, variant = "text" }: Back & { variant?: "text" | "button" }) {
  if (variant === "button") {
    return (
      <Button asChild variant="ghost" size="sm" className="-ml-2">
        <Link {...link}>
          <ArrowLeftIcon data-icon="inline-start" />
          {label}
        </Link>
      </Button>
    );
  }

  return (
    <Link {...link} className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm">
      <ArrowLeftIcon className="size-4" />
      {label}
    </Link>
  );
}

/**
 * The back link to the page named by the `from` search param, or `fallback` (the page's own list) without one.
 * A managed stack is named once its query answers; it is usually cached from the page the user came from.
 */
export function useBack(from: string | undefined, page: { environmentId?: string; stackId?: string }, fallback: Back): Back {
  const { t } = useTranslation();
  const link = originLink(parseOrigin(from), page);
  const stackId = link?.to === "/stacks/$stackId" ? link.params.stackId : undefined;
  const stack = useQuery({ ...stackQuery(stackId ?? ""), enabled: stackId !== undefined });

  switch (link?.to) {
    case "/stacks/$stackId":
      return { link, label: stack.data ? t("navigation.backTo", { name: stack.data.name }) : t("navigation.backToStack") };
    case "/stacks/unmanaged/$environmentId/$stackName":
      return { link, label: t("navigation.backTo", { name: link.params.stackName }) };
    case "/audit":
      return { link, label: t("navigation.backToAudit") };
    default:
      return fallback;
  }
}
