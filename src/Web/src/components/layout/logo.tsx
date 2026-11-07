import { cn } from "@/lib/utils";
import { LOGO_PATH } from "@/components/layout/logo-path";

/** The container-forest logo (gear and fir), drawn in the current text colour so it fits both themes. */
export function Logo({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 128 128" aria-hidden="true" className={cn("shrink-0", className)}>
      <path fill="currentColor" fillRule="evenodd" d={LOGO_PATH} />
    </svg>
  );
}
