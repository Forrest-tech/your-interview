# Bug Fix Log — UX Overhaul (2026-10-07)

Branch: `rabbit/ux-overhaul-20261007` (from 888070d)

## Bug 1: Language defaults to Chinese (user-reported)

**Root cause:** `I18nService.readInitial()` returned `'zh'` when no language was saved in localStorage. Additionally, dozens of components had hardcoded Chinese strings instead of using `t('key')`.

**Fixes:**
- `readInitial()` now defaults to `'en'`.
- Full i18n sweep across all features. Replaced ~500+ hardcoded Chinese UI strings with `t()`/`tn()`/`tf()` calls.
- Added 423 new DICT entries (DICT: 813 → 1236 keys), all with zh/en/fr.
- Removed static `STATUS_LABELS`/`OUTCOME_LABELS` maps in tracker (Chinese-only); now use reactive `i18n.t('status.'+s)` lookups that update on language switch.
- Same treatment for `ROUND_STAGE_LABELS`/`INVITE_FORMAT_LABELS` (smart-add dialog), `REVIEW_LABELS`/`MASTERY_LABELS` (techstack).
- Added `tf(key, params)` multi-placeholder method to I18nService (for strings like "Page {p} of {tp}").
- Widened `tn()`/`tf()` to accept `null | undefined` (Angular number pipe can return null).
- Fixed `readError()` in tracker: was a standalone function using `this.t()` (compile error); converted to private method.
- Fixed stray Chinese punctuation in dashboard (`。` after display name).
- Language switcher in shell still works for zh/fr (verified DICT has all three).

**Files:** `core/i18n/i18n.service.ts`, all `features/*` components (tracker, dashboard, playbook, techstack, mock, ai-practice, analytics, admin, profile, auth), `layout/shell`.

## Bug 2: Whole-page scrollbar on Tracker (user-reported)

**Root cause:** Shell `.content` had `overflow-y: auto`, so header + metrics + filters + board all scrolled together.

**Fixes:**
- Tracker page is now a fixed-height (100%) flex column: header/metrics/filters/pager are `flex: 0 0 auto` (fixed).
- `.board` is the only scroll region: `flex: 1`, columns scroll horizontally (`overflow-x: auto`), each `.col-body` scrolls vertically independently (`overflow-y: auto`, `scrollbar-width: thin`).
- Shell `.content` gets `overflow: hidden; padding: 0` for `app-tracker` (via `:has()` selector, same pattern as existing `app-ai-practice` rule).
- Same fixed-layout pattern applied to Playbook (card grid scrolls) and TechStack (list body already scrolled internally; page now fixed height).

**Files:** `layout/shell.component.scss`, `features/tracker/tracker.component.scss`, `features/playbook/playbook.component.scss`, `features/techstack/techstack.component.scss`.

## Bug 3: Bug hunt findings

| # | Bug | Severity | Status |
|---|-----|----------|--------|
| 3.1 | Dead "Continue answering" button in mock session cards (no click/routerLink) | Medium | **Fixed** — added `[routerLink]="['/mock', s.id]"` |
| 3.2 | Company logo `<img>` missing `alt` text (a11y) | Low | **Fixed** — added `[alt]="a.companyName"` |
| 3.3 | `tn()` type error when Angular number pipe returns null | Medium | **Fixed** — widened signature |
| 3.4 | `readError()` using `this` in standalone function (compile error) | High | **Fixed** — converted to private method |
| 3.5 | Missing DICT keys (89) referenced by worker-updated templates | High | **Fixed** — all entries created |
| 3.6 | Tracker `tf()` param type didn't accept `undefined` from optional chaining | Medium | **Fixed** — widened signature |

## UX Improvements (research-backed)

Based on research of Simplify.jobs, Teal, Huntr, and job-tracker UX patterns:

1. **Keyboard accessibility** (`styles.scss`): Added global `:focus-visible` ring (2px indigo) so keyboard users can see focus. Cards get proper focus shape.
2. **Touch-friendly actions** (tracker): `.app-actions` were `opacity: 0` until hover — invisible on mobile. Added `@media (hover: none)` to always show them on touch devices.
3. **Keyboard-operable cards** (tracker): Added `tabindex="0"`, `role="button"`, `aria-label`, and Enter/Space handlers to kanban cards.
4. **Empty states**: Verified tracker, playbook, mock all have actionable CTAs (already good — no changes needed).

**Not changed:** Visual identity, color scheme, Material components. Practical improvements only.

## Deferred

- **Backend i18n**: Some API error messages come from the backend in Chinese. Frontend displays them as-is. Out of scope (would require backend changes).
- **AI Practice page**: Large page, mostly converted. Some deeply nested dynamic strings may remain — low priority.
- **E2E visual testing**: No backend running locally; verified via `ng build` + static analysis. User should visually verify via tunnel.

## Verification

- `ng build` — **passes with zero errors** (only pre-existing bundle-size warnings).
- All 832 `t()`/`tn()`/`tf()` references resolve to DICT keys (verified by script).
- DICT quality check: all `en` values are English (except `lang.zh` = "中文" which is intentional).
