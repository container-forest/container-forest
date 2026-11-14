# Panel: conventions

Paths below are relative to `src/Web`. Individual screens are described in [docs/design/web-panel.md](../../docs/design/web-panel.md), along with `search.redirect` and sign-in.

## Tools and UI

- Vite 8, React 19, TypeScript **5.9**, not 7: `openapi-typescript` supports only TypeScript 5 (TS 7 has no JS API). Versions in `package.json` are exact (`--save-exact`); Node comes from `.nvmrc`.
- Two tsconfigs: `tsconfig.json` for the app (no Node types), `tsconfig.node.json` for `vite.config.ts`. The `@/` alias points to `src/`.
- UI: shadcn/ui (style `radix-nova`, `components.json`), Tailwind CSS 4 through `@tailwindcss/vite`, themes as CSS variables in `src/styles.css`. Google Fonts are bundled through `@fontsource` (the CSP doesn't allow CDNs): Manrope for the interface, JetBrains Mono for code, commits, paths and logs (`font-mono`). Add a component with `npm run ui:add -- <name>` into `src/components/ui`, not with `npx shadcn add` directly.
- Icons are Material Symbols (Rounded), not Lucide: shadcn generates components only with Lucide, Tabler, HugeIcons, Phosphor, Remix or Radix Icons, so `ui:add` (`scripts/ui-add.mjs`) rewrites the `lucide-react` imports to `@/components/icons` after `shadcn add` and drops `lucide-react` from the dependencies. `src/components/icons.ts` maps Lucide names (`CheckIcon`, `XIcon`, …) to Material icons; a new icon that isn't there fails typecheck, so add a line. `unplugin-icons` (with `@iconify-json/material-symbols` and `@svgr/core`) compiles icons into React components at build time: only the used ones reach the bundle, with no font or CDN. The files in `components/ui` are generated but may be edited.
- The panel's image is `src/Web/Dockerfile`, built from the repository root like the other images (`docker build -f src/Web/Dockerfile .`): built on Node 24 in `/repo/src/Web` next to `/repo/deploy/agent/compose.yaml`, served by Caddy (`src/Web/Caddyfile`, ADR-0005; `CF_SITE_ADDRESS`, `CF_AGGREGATOR_URL`, `CF_AGGREGATOR_GRPC`), with an SPA fallback to `index.html`, 404 for a missing asset, `/assets/` cached forever, and the CSP `default-src 'self'`. gRPC is recognised by `Content-Type: application/grpc*` and goes to the aggregator's gRPC port as h2c. Don't add inline scripts or styles from CDNs: the CSP blocks them.
- **Lists:** a row with its own page opens as a whole: the link on the name stretches over the row (`rowClass`, `rowLinkClass`, `src/lib/row-link.ts`; Cmd/Ctrl-click opens a tab), and buttons in a row are only quick or secondary actions (`rowActionsClass` lifts them above the link). There are no duplicate *Open* / *Logs* / *Manage* buttons. On narrow screens tables hide secondary columns (`hidden md:table-cell` etc.) rather than scrolling sideways.
- The base `DialogContent` and `AlertDialogContent` scroll when taller than the screen, let fields shrink and wrap long values; `SelectTrigger` is no wider than its field, and full width in dialogs.

## Data and routes

- The API is reached through `api` from `src/api/client.ts` (`openapi-fetch`, types from `schema.gen.ts`); `unwrap` turns a non-2xx response into an `ApiError` with problem details. Query doesn't retry on 401.
- The `queryOptions` factories in `src/api/queries.ts` are the only source of query keys.
- The Router is created with `context: { queryClient }`, `defaultPreload: "intent"`, `defaultPreloadStaleTime: 0`.
- Authenticated routes live under the pathless layout `_authed` (`beforeLoad` → `ensureQueryData(meQuery)`, 401 → redirect to `/login` with `search.redirect`; `/login` has `validateSearch` for `redirect`).
- No `fetch` in `useEffect`: data goes through Query, navigation through the Router.
- One SignalR connection per app through `HubProvider` (`src/components/live/hub-provider.tsx`, React Context), mounted inside `_authed`. SignalR events invalidate Query queries rather than writing to a separate store; which event makes which keys stale is `invalidationsFor` in `src/features/live/model.ts` (lists are `exact`, so a change in containers doesn't refetch open logs). After a lost connection (an aggregator upgrade) everything is refetched: events may have been lost. `EnvironmentsChanged` also calls `Resubscribe`. The connection state is `useLiveStatus()`, with an indicator in the header (`LiveIndicator`: *Live* / *Reconnecting…*). A polling screen can turn polling off while the connection is alive: `refetchInterval: pollingInterval(useLiveStatus(), ms)` (the deployment screens do this). The first start is retried with backoff up to 30 s; 401/403 aren't retried (*No updates*).
- Table filters live in search params (`validateSearch`).
- Screen logic without React (filters, rules for which actions are available, input parsing, the translation key of an error) lives in `src/features/<screen>/model.ts`; the screen in `src/routes` only shows state and calls it. Example: `src/features/containers/model.ts`.
- The TS client is generated from `src/Aggregator/Api/openapi/*.json` (`openapi-typescript` + `openapi-fetch`). Changed a DTO: rebuild `Api`, commit the JSON and regenerate the client. SignalR event types are written by hand.
- `routeTree.gen.ts` is committed and never edited by hand.

## Text, forms, permissions

- User-visible text goes only through `t()` (`react-i18next`), with keys in `src/i18n/locales/uk.ts` (the source of keys) and `en.ts` (the same `Translation` type): a missing translation or an unknown key breaks typecheck. Zod schema messages are translation keys (`"…" satisfies MessageKey`), translated at render time. Language: the one saved in `localStorage` (`cf-language`) → the browser's language → Ukrainian. The aggregator's error texts (`detail` in problem details, `CommandError.message`) stay in English; the panel shows its own translation for known cases by status or code. The bot is English only for now; Discord passes the user's language (`UserLocale`), so the bot can be translated later.
- Forms are React Hook Form + Zod. Global UI state is `useState`/Context; Zustand only when really needed. No Redux.
- Hiding buttons by permissions is cosmetic only; the real check happens on the aggregator.

## Pitfalls

- Radix `Select` (shadcn) calls `onValueChange` not only when the user picks: with the current value on mount, and with `""` while the value matches no item (for example, before the list loads). A handler that resets other filters or navigates must ignore `""` and an unchanged value, or navigation loops or loses search params.
- In a `redirect` from a `loader`, `search: (prev) => …` gets the previous location's params, not those of the target; redirect while keeping params in `beforeLoad`, which has the target route's `search`.
- The Vite dev server in a Linux container with a directory mounted from Windows gets no file change events: restart it after edits (or turn on polling).
- The Router gets its history from `createRobustBrowserHistory` (`src/lib/browser-history.ts`), not the standard one. `@tanstack/history` writes URLs with an unbound call `win.history.pushState(...)`. A browser extension that later wraps the History API and calls the native method with its own `this` then breaks URL updates: in Firefox, `'replaceState' called on an object that does not implement interface History`, the address stays `/`, and *Back* closes the tab. Don't remove it until TanStack fixes the call.
- In a hidden browser tab (`document.visibilityState = hidden`, including a tab driven by automation) TanStack Query doesn't run `refetchInterval` and pauses retries, and Radix close animations don't finish: a dialog stays in the DOM with `data-state="closed"`. These aren't bugs in the screen; check such things in a visible tab.

## Library notes

Why each library was chosen: [docs/dependencies.md](../../docs/dependencies.md).

- **i18next** initialises asynchronously by default; translations are bundled, so `initAsync: false` makes them available on the first render and screens don't flash keys.
- **TanStack Query.** Requests failing with 401 aren't retried: a finished session would only delay the redirect to sign-in. On sign-out use `removeQueries()`, not `clear()`: `clear()` also drops the mutation cache, so the mutation's own callbacks (the navigation to `/login`) may not run.
- **TanStack Router.** The `redirect` search param is an arbitrary path with its query string, which `navigate({ to })` doesn't accept by type: use `navigate({ href, replace: true })` (`replace` so *Back* doesn't return to the sign-in form). Devtools are loaded under `import.meta.env.DEV` only.
- **React Hook Form.** The shadcn checkbox is a Radix button with `role="checkbox"`, not an `<input>`, so `register` doesn't work with it: use `Controller` with `checked` / `onCheckedChange`, and coerce the value to `boolean` (`onCheckedChange` can pass `"indeterminate"`).
