# Панель: конвенції

Шляхи нижче відносно `src/Web`. Опис окремих екранів — [docs/design/web-panel.md](../../docs/design/web-panel.md); `search.redirect` і вхід — там само.

## Інструменти й UI

- Vite 8, React 19, TypeScript **5.9**, а не 7: `openapi-typescript` підтримує лише TypeScript 5 (TS 7 не має JS API). Версії в `package.json` точні (`--save-exact`), Node — з `.nvmrc`.
- Два tsconfig: `tsconfig.json` для застосунку (без типів Node), `tsconfig.node.json` для `vite.config.ts`. Аліас `@/` → `src/`.
- UI: shadcn/ui (стиль `radix-nova`, `components.json`), Tailwind CSS 4 через `@tailwindcss/vite`, теми — CSS-змінні в `src/styles.css`, шрифти з Google Fonts вбудовуються в збірку через `@fontsource` (CSP не пускає CDN): Manrope для інтерфейсу, JetBrains Mono для коду, комітів, шляхів і логів (`font-mono`). Компонент додається командою `npm run ui:add -- <назва>` у `src/components/ui`, а не `npx shadcn add` напряму.
- Іконки — Material Symbols (Rounded), не Lucide: shadcn генерує компоненти лише з Lucide, Tabler, HugeIcons, Phosphor, Remix або Radix Icons, тому `ui:add` (`scripts/ui-add.mjs`) після `shadcn add` переводить імпорти `lucide-react` на `@/components/icons` і прибирає `lucide-react` із залежностей. `src/components/icons.ts` зіставляє lucide-назви (`CheckIcon`, `XIcon`, …) з іконками Material; нова іконка, якої там немає, валить typecheck — додати рядок. Іконки компілює в React-компоненти `unplugin-icons` (з `@iconify-json/material-symbols` і `@svgr/core`) під час збірки: у бандл потрапляють лише використані, без шрифту й CDN. Ці файли згенеровані: їх можна правити, але в облік лаби 2 вони нікому не йдуть; зараховуються екрани й власні компоненти поза `components/ui`. Для лаби 3 бібліотеки тут — Tailwind CSS і Radix UI (shadcn — генератор коду, а не бібліотека).
- Образ панелі — `src/Web/Dockerfile`, контекст — корінь репозиторію, як для інших образів (`docker build -f src/Web/Dockerfile .`): збірка на Node 24 у `/repo/src/Web` поруч із `/repo/deploy/agent/compose.yaml`, роздача Caddy (`src/Web/Caddyfile`, ADR-0005; `CF_SITE_ADDRESS`, `CF_AGGREGATOR_URL`, `CF_AGGREGATOR_GRPC`), SPA-fallback на `index.html`, відсутній asset — 404, `/assets/` кешуються назавжди, CSP `default-src 'self'`. gRPC розпізнається за `Content-Type: application/grpc*` і йде на gRPC-порт ЦА як h2c. Інлайн-скриптів і стилів з CDN у застосунку не додавати: CSP їх заблокує.
- **Списки**: рядок з окремою сторінкою відкривається весь — посилання на назві розтягується на рядок (`rowClass`, `rowLinkClass`, `src/lib/row-link.ts`; Cmd/Ctrl-клік відкриває вкладку), а кнопки в рядку — лише швидкі або другорядні дії (`rowActionsClass` піднімає їх над посиланням). Дублюючих кнопок «Відкрити» / «Логи» / «Керувати» немає. На вузькому екрані таблиці ховають другорядні колонки (`hidden md:table-cell` тощо), а не прокручуються вбік.
- Базові `DialogContent` і `AlertDialogContent` прокручуються, коли вищі за екран, дозволяють полям звужуватися й переносять довгі значення; `SelectTrigger` не ширший за своє поле, у діалогах — на всю ширину.

## Дані й маршрути

- Доступ до API — `api` з `src/api/client.ts` (`openapi-fetch`, типи з `schema.gen.ts`); `unwrap` перетворює не-2xx на `ApiError` з problem details. На 401 Query не повторює запит.
- `queryOptions`-фабрики в `src/api/queries.ts` — єдине джерело ключів запитів.
- Router створюється з `context: { queryClient }`, `defaultPreload: "intent"`, `defaultPreloadStaleTime: 0`.
- Авторизовані маршрути лежать під pathless layout `_authed` (`beforeLoad` → `ensureQueryData(meQuery)`, 401 → redirect на `/login` з `search.redirect`; `/login` має `validateSearch` для `redirect`).
- Жодного `fetch` у `useEffect`: дані через Query, переходи через Router.
- Одне SignalR-з'єднання на застосунок через `HubProvider` (`src/components/live/hub-provider.tsx`, React Context), змонтований усередині `_authed`. Події SignalR інвалідовують запити Query, а не пишуть у окремий стор; яка подія які ключі робить застарілими — `invalidationsFor` у `src/features/live/model.ts` (списки — `exact`, щоб зміна контейнерів не перечитувала відкриті логи). Після втраченого з'єднання (оновлення ЦА) перечитується все: події могли загубитися. `EnvironmentsChanged` ще й викликає `Resubscribe`. Стан з'єднання — `useLiveStatus()`, індикатор у шапці (`LiveIndicator`: «Наживо» / «Перепідключення…»). Екран, що опитує, може вимикати опитування, поки з'єднання живе: `refetchInterval: pollingInterval(useLiveStatus(), ms)` (так зроблено на екранах деплоїв). Перший старт повторюється з backoff до 30 с, 401/403 — без повторів («Без оновлень»).
- Фільтри таблиць — у search params (`validateSearch`).
- Логіка екрана без React (фільтри, правила доступності дій, розбір введення, ключ перекладу помилки) — у `src/features/<екран>/model.ts`; екран у `src/routes` лише показує стан і викликає її. Приклад — `src/features/containers/model.ts`.
- TS-клієнт генерується з `src/Aggregator/Api/openapi/*.json` (`openapi-typescript` + `openapi-fetch`). Змінив DTO — перезбери `Api`, закомить JSON і перегенеруй клієнт. Типи подій SignalR пишуться вручну.
- `routeTree.gen.ts` комітиться, руками не редагується.

## Текст, форми, права

- Текст для користувача — лише через `t()` (`react-i18next`), ключі — у `src/i18n/locales/uk.ts` (джерело ключів) і `en.ts` (той самий тип `Translation`): пропущений переклад або неіснуючий ключ ламають typecheck. Повідомлення схем Zod — ключі перекладу (`"…" satisfies MessageKey`), перекладаються під час рендеру. Мова: збережена в `localStorage` (`cf-language`) → мова браузера → українська. Тексти помилок ЦА (`detail` у problem details, `CommandError.message`) лишаються англійськими; відомі випадки панель показує власним перекладом за статусом або кодом. Бот поки лише англійською; Discord передає мову користувача (`UserLocale`), тож переклад бота можна додати пізніше.
- Форми — React Hook Form + Zod. Глобальний UI-стан — `useState`/Context; Zustand лише за реальної потреби. Redux — ні.
- Приховування кнопок за правами — лише косметика; реальна перевірка на ЦА.

## Пастки

- Radix `Select` (shadcn) викликає `onValueChange` не лише на вибір користувача: з поточним значенням під час монтування і з `""`, поки значення не збігається з жодним пунктом (наприклад, до завантаження списку). Обробник, що скидає інші фільтри або навігує, має ігнорувати `""` і незмінене значення, інакше навігація зациклюється або губить search params.
- У `redirect` з `loader` функція `search: (prev) => …` отримує параметри попереднього розташування, а не того, куди йде перехід; перенаправлення зі збереженням параметрів робити в `beforeLoad`, де є `search` цільового маршруту.
- Vite dev-сервер у Linux-контейнері з каталогом, змонтованим із Windows, не отримує подій зміни файлів: після правок його треба перезапускати (або вмикати polling).
- Router отримує історію з `createRobustBrowserHistory` (`src/lib/browser-history.ts`), а не стандартну. `@tanstack/history` пише URL неприв'язаним викликом `win.history.pushState(...)`. Розширення браузера, що пізніше підміняє History API й викликає нативний метод зі своїм `this`, через це ламає запис URL: у Firefox — `'replaceState' called on an object that does not implement interface History`, адреса лишається `/`, «Назад» закриває вкладку. Не прибирати, доки TanStack не виправить виклик.
- У прихованій вкладці браузера (`document.visibilityState = hidden`, зокрема вкладка, якою керує автоматизація) TanStack Query не виконує `refetchInterval` і ставить на паузу повтори запитів, а анімації закриття Radix не завершуються: діалог лишається в DOM з `data-state="closed"`. Це не помилки екрана; перевіряти такі речі треба у видимій вкладці.
