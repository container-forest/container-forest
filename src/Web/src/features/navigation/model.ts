/** The parts of the panel that belong to one environment, in the order the sidebar lists them. */
export const environmentSections = ["stacks", "containers", "keys", "settings"] as const;

export type EnvironmentSection = (typeof environmentSections)[number];

/** Where the current environment is remembered between visits, like the language. */
export const ENVIRONMENT_STORAGE_KEY = "cf-environment";

/** Which environment section a path belongs to; a stack's pages and its deployments count as stacks. */
export function sectionOf(pathname: string): EnvironmentSection | undefined {
  if (pathname.startsWith("/stacks") || pathname.startsWith("/deployments")) {
    return "stacks";
  }

  if (pathname.startsWith("/containers")) {
    return "containers";
  }

  if (pathname.startsWith("/keys")) {
    return "keys";
  }

  // The list of all environments is administration; only one environment's page is its settings.
  return /^\/environments\/[^/]+/.test(pathname) ? "settings" : undefined;
}

/**
 * The environment the open page is about: a path parameter wins over the `environment` search parameter, which wins
 * over the environment of the stack the page shows. Undefined for pages about no environment.
 */
export function environmentOfPage(page: {
  environmentIdParam?: string;
  environmentSearch?: string;
  stackEnvironmentId?: string;
}): string | undefined {
  return page.environmentIdParam ?? page.environmentSearch ?? page.stackEnvironmentId;
}

/**
 * The environment the sidebar works with: the page's own if the user can see it, else the remembered one, else the
 * first. Undefined only when the user sees no environment at all.
 */
export function chooseEnvironment(
  environments: readonly { id: string }[],
  fromPage: string | undefined,
  remembered: string | undefined,
): string | undefined {
  const visible = (id: string | undefined) => id !== undefined && environments.some((e) => e.id === id);
  if (visible(fromPage)) {
    return fromPage;
  }

  return visible(remembered) ? remembered : environments[0]?.id;
}

export function readRememberedEnvironment(): string | undefined {
  try {
    return localStorage.getItem(ENVIRONMENT_STORAGE_KEY) ?? undefined;
  } catch {
    // Storage can be off (private mode, policies): the first environment is a fine default.
    return undefined;
  }
}

export function rememberEnvironment(id: string): void {
  try {
    localStorage.setItem(ENVIRONMENT_STORAGE_KEY, id);
  } catch {
    // Not remembering is harmless.
  }
}
