import { type ComponentProps, useState } from "react";
import { useTranslation } from "react-i18next";
import { CheckIcon, CopyIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";

/**
 * Copies `text` to the clipboard and says so on the button for a moment. The clipboard needs a secure origin
 * (https or localhost); without it the button says that copying failed, and the text stays selectable on screen.
 */
export function CopyButton({
  text,
  label,
  variant = "outline",
  size = "sm",
}: {
  text: string;
  label: string;
  variant?: ComponentProps<typeof Button>["variant"];
  size?: ComponentProps<typeof Button>["size"];
}) {
  const { t } = useTranslation();
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setState("copied");
    } catch {
      setState("failed");
    }

    setTimeout(() => setState("idle"), 2_000);
  };

  return (
    <Button type="button" variant={variant} size={size} onClick={() => void copy()} aria-live="polite">
      {state === "copied" ? <CheckIcon data-icon="inline-start" /> : <CopyIcon data-icon="inline-start" />}
      {state === "copied" ? t("copy.copied") : state === "failed" ? t("copy.failed") : label}
    </Button>
  );
}
