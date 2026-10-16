import { ForestIcon } from "@/components/icons";
import { cn } from "@/lib/utils";

/** The name of the panel with its small forest mark, as it appears in the sidebar, the phone bar and on sign-in. */
export function Brand({ className }: { className?: string }) {
  return (
    <span
      className={cn(
        "font-heading inline-flex items-center gap-1.5 font-extrabold tracking-tight whitespace-nowrap",
        className,
      )}
    >
      <ForestIcon aria-hidden className="text-primary size-[1.25em] shrink-0" />
      <span>
        container<span className="text-primary">-</span>forest
      </span>
    </span>
  );
}
