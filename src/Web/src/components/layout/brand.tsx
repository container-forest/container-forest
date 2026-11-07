import { cn } from "@/lib/utils";
import { Logo } from "@/components/layout/logo";

/** The name of the panel with its logo, as it appears in the sidebar, the phone bar and on sign-in. */
export function Brand({ className }: { className?: string }) {
  return (
    <span className={cn("font-heading inline-flex items-center gap-2 font-extrabold tracking-tight whitespace-nowrap", className)}>
      <Logo className="size-[1.6em]" />
      <span>
        container<span className="text-primary">-</span>forest
      </span>
    </span>
  );
}
