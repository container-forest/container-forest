import { useEffect } from "react";
import { rememberListSearch } from "@/features/navigation/back";

/** Keeps a list screen's current filters for the back links of the pages it opens (`rememberedListSearch`). */
export function useRememberListSearch(...args: Parameters<typeof rememberListSearch>): void {
  const [list, search] = args;
  useEffect(() => {
    rememberListSearch(list, search);
  }, [list, search]);
}
