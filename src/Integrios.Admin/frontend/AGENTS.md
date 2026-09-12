# Operator dashboard instructions

The repository-root `AGENTS.md` governs the whole solution and also applies here. This file adds
durable rules for the Operator dashboard SPA under `src/Integrios.Admin/frontend`.

## Commands

Run these from this directory.

```bash
npm run dev            # Vite dev server; proxies /admin and /auth to the Admin host
npm run check          # Biome, generated API, TypeScript, and Vitest
npm run build          # Full check, then production assets in ../wwwroot
npm run lint:fix       # Apply Biome formatting and safe fixes
npm run generate:api   # Regenerate src/api/schema.d.ts from Admin OpenAPI
```

Use the narrowest relevant test while iterating:

```bash
npx vitest run src/screens/Events.test.tsx
npx vitest run -t "applies the summary window"
npx vitest run tests/e2e/forms.browser.test.ts
```

`generate:api` builds `Integrios.Admin.csproj`, so it requires the .NET SDK. The development proxy
targets `http://localhost:5150` unless `INTEGRIOS_ADMIN_ORIGIN` overrides it.

## Vocabulary

These name the dashboard's own shapes. Use them in code, comments, tests, and commit messages; the
rules that govern each shape live in the sections below. Product language — Operator, Event, Topic,
Delivery — belongs to the solution and is not restated here.

**Screen** — a route-level component in `src/screens`, named for the capability it serves, owning
its queries, its forms, and its copy. 
_Avoid_: "page" for the component; `Page` is the layout box a
screen renders into.

**Filter bar** — the wrapping row of controls that states a list's current scope. 
_Avoid_:
"toolbar", "search bar".

**List states** — a list is in exactly one, and the words are not interchangeable. The **skeleton**
covers a first read in flight. The **empty card** replaces the table when an unfiltered list holds
nothing and carries the screen's create action. The **filtered-empty sentence** is the one line a
list gets when its own filters excluded everything. The **table card** holds the rows. A failed read
is the **read error**. 
_Avoid_: "empty state" on its own, which collapses the two empty cases that
are deliberately different shapes.

**Sheet** — the trailing-edge overlay authoring opens (`CreateSheet`, `EditSheet`). 
_Avoid_:
"modal", "drawer".

**Confirmation** — the centred dialog an irreversible action opens (`ConfirmAction`). It is not a
sheet: different shape, different job.

**Inspector** — the detail beside the list it was selected from, inside a `SplitView`. The column
held open before anything is selected is the **inspector placeholder**, which carries a sentence and
no border rather than being an empty inspector. 
_Avoid_: "panel" — a `Panel` is the bordered box a
form or a read-only group sits in, wherever it appears.

**Ledger** — the Events list, and only it: the one list read for history rather than for
configuration.

**Disclosure** — a collapsed section inside a form, for material that is generated or pasted rather
than authored field by field. 
_Avoid_: "accordion".

## API boundary

- Treat `src/api/schema.d.ts` as generated output. Change the Admin API and run `generate:api`; never
  hand-edit the generated schema.
- Derive API types, including `OperatorSession`, from the generated schema rather than redeclaring
  matching frontend types.
- Keep the deployed dashboard same-origin with the Admin host. `api/client.ts` owns the base URL and
  antiforgery middleware; do not introduce per-screen clients or CORS configuration.
- Send sign-out through its native form with the server-provided antiforgery form field. Unsafe API
  requests use the antiforgery header installed by the shared client.

## Data fetching and failures

- Route typed client calls through `call()` in `api/query.ts`. Non-success responses become
  `Problem` values consumed by TanStack Query.
- Render both halves of a rejected write: `applyProblem` places field-keyed messages on their
  controls, while `formError` or `FormError` renders unassigned messages and the empty-message
  fallback. Match server field keys case-insensitively.
- Model Admin lists as `useInfiniteQuery` plus `nextCursor` and an explicit `LoadMore`. Do not add
  totals, page numbers, offsets, or automatic infinite scrolling unless the API contract changes.
- Include every active filter in the query key. A cursor belongs only to the filter set that issued
  it.
- Invalidate affected queries after a successful mutation and re-read authoritative server state;
  do not maintain a second patched copy of server data.

## Routing and scope

- Treat the route as the Tenant selection. Never infer Tenant ownership from the signed-in Operator
  session.
- Validate route identifiers with `isIdentifier` before calling the API. A malformed identifier
  renders Not found.
- Put Tenant-scoped selection in the route and list filters in the query string so links, refresh,
  and browser navigation restore the same view.
- Give routes a `handle.section` value and let the shell use it for navigation and breadcrumbs;
  screens must not infer their navigation section from path text.
- Keep `/` as an alias of the Tenants list rather than a redirect so query strings and fragments are
  preserved.

## Screen and authoring patterns

- Use `PageHeader.action` for the primary screen action. Create and edit forms open through
  `CreateSheet` and `EditSheet`; irreversible actions use `ConfirmAction` and name their target and
  consequence before confirmation.
- Let the screen own a sheet's open state wherever more than one control opens it, and render
  `SheetButton`s. The buttons hold nothing, so a layout decision that removes one cannot close a
  form an Operator has started filling in.
- Build forms with Zod, `react-hook-form`, and the field wrappers in `ui/fields.tsx`. Keep each
  form's capability-specific schema, conversion, request, and mutations in its owning screen.
- Keep shared UI modules limited to repeated presentation or interaction behavior. Do not introduce
  a generic schema-driven authoring form or move capability semantics into `ui/`.
- Treat the Admin API as authoritative for domain validation. Browser validation may catch syntax,
  required controls, and safe local conversions, but must not duplicate server-owned contract
  interpretation.
- Use the vendored Radix listbox through `SelectField` for authored form choices. It is not a native
  `<select>`: test open-menu keyboard and pointer behavior in a real browser, not with
  `fireEvent.change`.
- Keep source-side and subscription-side mappings distinct even when their editors reuse path and
  expression helpers.

## Lists and operational state

- Put free-text search first, full-width, and borderless at rest; place select filters after it.
- Keep filters visible in a wrapping row over any list that has rows or is already under a filter,
  and render no filter bar until the read says which. Applied filters state their value and use a
  selected treatment; show one conditional Clear action when anything is applied.
- State the loaded-row count in the list caption. Do not present it as a server total.
- Use shape-matched skeletons that reserve loaded layout. A filtered-empty sentence names the scope
  it searched and leaves the way out to the filter bar's Clear action; an unfiltered-empty list gets
  the empty card instead, which carries the screen's create action because the page header has
  dropped its own copy.
- Make every successful write visibly acknowledge completion.
- Pair every status colour with text. Use sentence-case status labels and `tabular-nums` for numeric
  and time columns.

## Styling and accessibility

- Keep the dashboard a dense, still Operator console: no hero sections, ambient decoration,
  scroll-entry effects, staggered mounts, or macro whitespace.
- Use Tailwind utilities on the markup that owns presentation. `src/index.css` owns the single
  semantic token set and global accessibility defaults; do not add another global stylesheet or
  duplicate palette values in components.
- Keep the interface light-only, with flat bordered surfaces and no decorative shadows or
  gradients.
- Use `lucide-react` for navigational or functional icons. Icons are `aria-hidden` and supplement a
  visible label; they never carry meaning alone.
- Preserve visible focus, keyboard operation, semantic labels and headings, a 24-by-24 CSS-pixel
  minimum target, non-colour status communication, and the global reduced-motion behavior.
- Keep authoring sheets and dialogs usable without horizontal document overflow at 320 CSS pixels.

## Testing

- Use `src/**/*.test.tsx` for jsdom behavior. Create a fresh query client per test and use
  `test/router.tsx` plus `test/http.ts` so tests exercise real routing, typed requests, and Problem
  handling.
- Use `screens/accessibility.test.tsx` for axe checks that do not require layout. Keep colour
  contrast and target-size checks in real-browser tests because jsdom cannot compute them.
- Use `tests/e2e/*.browser.test.ts` for focus order, visible focus, custom listbox interaction,
  responsive layout, and other browser-owned behavior.
- Run `npm run check` for a completed code change and `npm run build` when production assets or the
  final frontend gate are in scope.

## Comments

Use `///` comments only for constraints or domain reasons that are not evident from types and code.
Do not use them to narrate implementation steps or record temporary project status.
