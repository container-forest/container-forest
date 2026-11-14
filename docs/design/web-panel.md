# Panel screens

Only what isn't visible from a screen's code. Paths are relative to `src/Web`. Shared conventions: [src/Web/CLAUDE.md](../../src/Web/CLAUDE.md).

## Sign-in and users

- Only the dev server's proxy handles `CF_DEV_USER` (`vite.config.ts`); the app's code has no dev authentication.
- `/login`: signing in drops the previous user's cache and puts the response into `meQuery`; signing out drops the cache. A 401 is one message, with no hints about the password policy.
- `search.redirect` accepts only paths on the same site (`/…`, not `//…`) to prevent an open redirect; after signing in, `navigate({ href: redirect ?? "/", replace: true })`.
- Users: every mutation in `src/api/users.ts` invalidates `usersQuery.queryKey`, so both the list and the card refresh. The aggregator's reasons for rejecting a password (`errors` in problem details) are shown as they are, in English, under a translated heading (`PasswordReasons`).

## Navigation, appearance, PWA

- **Navigation:** `SidebarContent` (`src/components/layout/app-sidebar.tsx`). The current environment comes from the page itself (the `environmentId` path param, the `environment` search param, a stack's environment), else the remembered one (`localStorage` `cf-environment`), else the first (`src/features/navigation/model.ts`). Switching environments stays in the same section. Stack, container and key pages have no environment picker of their own, only the name in the header.
- **Theme:** light and dark, following the device by default. `ThemeToggle` next to the language (sidebar, sign-in) stores the choice in `localStorage` `cf-theme`; a choice equal to the device's theme is erased, and the panel follows the device again (`src/features/theme/model.ts`). `public/theme-init.js` sets the `dark` class before the first paint by the same rule (a separate file: the CSP blocks inline scripts). Colour themes: *Forest* (our own, the default), *Meadow* (light on pale green instead of white with its own tones; dark is Catppuccin Mocha with green) and three based on the [Catppuccin](https://github.com/catppuccin/palette) palette (MIT): *Lilac* (Latte / Macchiato, mauve, deepened to `#6411d0` / `#aa71f2`), *Lake* (Latte / Frappé, blue, deepened to `#0947c3` / `#7196ea`), *Sunset* (Latte / Mocha, peach). Each has a light and a dark variant, so the theme toggle works in any of them. You pick one from the mushrooms on the little meadow at the bottom of the *Colour theme* card on the *Appearance* page (`/appearance`) (`PalettePicker`: each mushroom's cap is its theme's accent because the `<svg>` has its own `data-palette`; the meadow uses the current theme's colours; the chosen one is highlighted), stored in `localStorage` `cf-palette`, as the `data-palette` attribute on `<html>` (also set by `theme-init.js`); the themes' CSS variables are in `src/styles.css`, the preview and browser bar colours in `paletteSwatches`. In light Latte, buttons have enough contrast (WCAG AA ≥ 4.5): deepened mauve and blue (7.3 and 7.8), peach deepened to `#c64e09` with white; Latte's green (3:1) wasn't used.
- **Seasonal backdrop:** `SeasonalBackdrop` (`src/features/seasons/model.ts`): leaves, butterflies with poplar fluff, falling leaves or snowflakes depending on the season. The *Appearance* page (`/appearance`, under *My settings* in the sidebar; a browser setting, not an environment one, so not in the environment's *Settings*) has the *Seasonal backdrop* card and *Colour theme* below it: on/off, the season (by the calendar, or one for the whole year; the same list has *Downpour*, heavy rain the calendar never turns on: dense, long, thick slanting streaks, slow and muted so they don't tire the eyes). Everything is on by default; the setting is `localStorage` `cf-backdrop` (the old `cf-season` is migrated, the former weather `rain` field is ignored). Snowflakes, fluff and the downpour have separate colours in the light theme. Density is `density` (particles per 100,000 px²) within `minCount`/`maxCount`: on Full HD that's ~45 leaves, ~105 snowflakes, ~85 bits of fluff and ~18 butterflies, and up to 240 streaks in a downpour.
- With `prefers-reduced-motion` and in a hidden tab the backdrop doesn't draw. It shows only through transparent areas: cards and the sidebar cover it, and it must stay that way.
- **PWA:**
  - `scripts/pwa-icons.sh` renders the icons in `public/icons` from `public/logo.svg` and `pwa/*.svg`; the PNGs are committed.
  - The service worker is the `pwa/sw.js` template, which the `pwaServiceWorker` plugin in `vite.config.ts` completes at build time with the list of all the version's files. The cache is named by the list's hash, and the old one is deleted.
  - The SW never touches `/api/*`, `/hubs/*`, non-GET requests or other origins; navigation is network first, falling back to the cached `index.html`.
  - It's registered only in a production build (`setUpApp` in `src/features/pwa/model.ts`). Offline, `_authed` shows `ConnectionError` and reloads itself on the `online` event.
  - To check locally: `npm run build && npm run preview`. Caddy serves the manifest as `application/manifest+json`; `sw.js` and the icons are `no-cache`.

## Containers and logs

- Shared components: `ContainerTable` (the containers screen and `StackContainersCard` on managed and unmanaged stack pages), `StackQuickActions` (the same order of icons as for containers).
- `/containers`: without `environment`, or with an invisible environment, `beforeLoad` redirects to the first one, keeping the filters. Polling stays on even with a live connection because older agents don't send events (`pollingInterval`). Which buttons are available follows the aggregator's `ContainerCommand` rules. Result messages are kept as translation keys so switching the language translates them.
- Removing a container is the fifth button of `ContainerTable`, only with `canRemove` and for a stopped container. Its confirmation has no stop timeout (`takesStopTimeout`).
- `/stacks`: filters in search params, as on the containers screen. `kind` is all, managed or unmanaged; `state` is running (a managed stack deployed, an unmanaged one with containers) or not; `q` searches by name, repository, branch, compose file and directory. The rules are `filterStacks` and `filterUnmanagedStacks` in `src/features/stacks/model.ts`. `beforeLoad` keeps the filters when it fills in the environment. *Refresh* rereads both lists.
- Unmanaged stacks (ADR-0016) are a section under the table on `/stacks`, `UnmanagedStacksSection`, and the stack page `/stacks/unmanaged/$environmentId/$stackName`; details in [unmanaged-stacks.md](unmanaged-stacks.md#panel).
- Logs (`/containers/$environmentId/$containerId/logs`, file `containers_.…logs.tsx`: the `_` takes it out from under the `/containers` route):
  - The container in the path is a name, not an id, so the link survives compose recreating the container.
  - `containerLogsQuery` takes the tail (`tail`) the first time, and each refetch only lines with `since` = the latest time among those loaded, appending them (`mergeLines`). A different `tail` is a different key and a fresh load.
  - The `since` boundary is inclusive, so lines with the same time are dropped when they match (stream + text) one already loaded.
  - `src/features/container-logs/ansi.ts` parses ANSI colours; the 16 basic ones are the CSS variables `--ansi-0…15`. Without colours, and in search, copying and the downloaded file, escape sequences are stripped.
  - The request isn't retried for 404, 501 (an agent that can't read logs) and 503: otherwise the screen would sit on *Loading* for a long time.

## Environments

- **The enrollment token is shown once:** only the state of the component that got the response holds it (the creation dialog or the *Agent enrollment* section), not the Query cache, the URL or history state. Mutations carrying a token or a registry password have `gcTime: 0` and are reset right after success, so the response and variables don't stay in the mutation cache. The credentials form is cleared after saving.
- A new token when one has been issued or an agent is enrolled asks for confirmation: the old one stops working, and an agent with the new one will replace the current one.
- Files for the host: `compose.yaml` is `deploy/agent/compose.yaml` itself, embedded at build time (`import … from "@deploy/agent/compose.yaml?raw"`; the `@deploy` alias and `server.fs.allow` are in `vite.config.ts`), so the screen can't drift from the file in the repository. The token goes only into `.env` (`agentEnvFile`), not into the compose file, which people may commit.

## Passkeys, tokens, repositories

- **Passkeys** (ADR-0009): the model is `src/features/passkeys/model.ts`; `NotAllowedError`/`AbortError` mean the person cancelled, not an error. On `/login` the autofill ceremony starts in a `useEffect` with an `AbortController` (an exception to "no fetch in useEffect": it isn't loading data but a ceremony that has to wait until a field is focused), and the button cancels it first; *Remember me* is read when a key is picked.
- **API tokens:** a token is shown once in the creation dialog, only in component state, and the mutation with `gcTime: 0` is reset right after success. `TokenTable` is shared with the tokens card on a user's screen (`UserTokensCard`, where an administrator can only revoke).
- **Repositories and connections:** administrators and editors see the *Repositories* link (`canSeeRepositories`); *Connections* is a button on the repositories screen for administrators. `RepositoryFields` checks that a URL is under the prefix with the same logic as the aggregator (`coversUrl`, `src/features/git-connections/model.ts`). The `PUBLIC_REPOSITORY` value means no connection. A connection's token is handled like a registry password: mutations with `gcTime: 0`. `WebhookSection` is shared by repositories and connections.

## Image updates

ADR-0015, `src/components/images`, `src/features/images/model.ts`, `src/api/images.ts`.

- On a stack's screen: `ImagePoliciesCard` (policies, the last check's state, *What would change* for everyone who sees the stack; *Check now*, pinning a tag, unpinning, changing and removing for an Editor; the latest update commits) and `ImageHookCard` (the token is shown once, only in component state; the mutation has `gcTime: 0`).
- In the connection dialog: `PushTokenSection`, the write token next to the access token.
- On the connections screen an administrator sees `ImageUpdatesAdminCard`: the aggregator's public key with copy and rotate, retired keys, registry polling and its interval (fields set by the aggregator's environment are locked).

## Backup

`/backup`, administrators only (`src/routes/_authed/backup.tsx`, `src/components/backup`, `src/features/backup/model.ts`). The state and *Back up now*, the settings form, the latest runs. A field set by an environment variable is shown but can't be edited. A secret never comes from the aggregator: an empty field keeps the stored key, and the field is cleared after saving. *Save* first opens the identity confirmation dialog (`ConfirmIdentityDialog`, shared with adding a passkey), whose token goes in the same request. Description: [settings and backup](backup.md).
