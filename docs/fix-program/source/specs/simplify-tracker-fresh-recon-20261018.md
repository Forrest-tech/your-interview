# Fresh Recon: Simplify.jobs Tracker → your-interview

**Date:** 2026-10-18 | **Fidelity:** public-docs + code-trace (no login, no live clicks)
**Method:** Simplify official help center (help.simplify.jobs, all updated Oct 2026), Chrome Web Store listing, review sites. your-interview side traced from `web/src/app/features/tracker/` source.
**Clean-room:** function and flow only, no code copied. Features described in own words.

> ⚠️ What this report is NOT: I could not log in to simplify.jobs (subagent, no live browser). Interaction-level details (exact click counts, drawer widths, animation behavior) are inferred from official docs and reviews, marked where uncertain. A browser-capable agent should click through the 4 help-center flows to confirm UX details before implementation.

---

## 1. Simplify tracker — feature inventory

### 1.1 Capture (how jobs get in)
| ID | Feature | Function | Flow |
|---|---|---|---|
| C1 | Auto-add on apply | Any application submitted through Simplify's job board/matches lands in the tracker automatically with status + details prefilled | apply on Simplify → card appears in tracker, no manual step |
| C2 | Copilot post-submit prompt | After submitting an application via the Copilot extension on any site, user is prompted to add it to the tracker with details prefilled | submit → extension asks "add to tracker?" → confirm → logged |
| C3 | Save-from-posting | On any job posting, open Copilot → click the job title at top → "save job to tracker" form with details prefilled from the posting → review/edit → save | posting → extension panel → title → save form → saved to Saved section |
| C4 | Manual add | "+ Add Application" button top-right of tracker, fill fields manually | tracker → + Add Application → form → save |
| C5 | CSV import | "Import CSV" top action row; required columns: Company name, Position Title; optional: Location | tracker → Import CSV → file → bulk create |

### 1.2 Pipeline management
| ID | Feature | Function | Flow |
|---|---|---|---|
| P1 | Columns view | Kanban grouped by application stage | toggle in top action row |
| P2 | List view | Table view of same data | same toggle |
| P3 | Drag between columns | Move card = change status | drag card → drop column → status updates |
| P4 | Status dropdown (list view) | Change status inline per row | row → Status dropdown → pick |
| P5 | Per-job panel status | "Application Status" field in the job panel's Overview tab | open job → Overview → Application Status |
| P6 | Favorite (heart) | Star an application for quick access | click heart icon on card/row |
| P7 | Archive | Moves app out of Active view without deleting; for rejections/stale/clutter | per-row Archive, or panel header Archive, or "Archive all" for current view, or "Manage Jobs" for cross-view bulk |
| P8 | Active / Archived sub-tabs | Two tabs separating live pipeline from archived | sub-tab next to Active |
| P9 | Unarchive | Restore archived app to Active | Archived tab → unarchive |
| P10 | Permanent delete | Delete from Archived sub-tab, or directly via panel header Delete | warns: "Deleted jobs can't be recovered. If you're not sure, archive first" |

### 1.3 Find & filter
| ID | Feature | Function | Flow |
|---|---|---|---|
| F1 | Filter row | Narrow by company, role, applied-from date, applied-until date, status, job type | filter row above tracker |
| F2 | Saved filtered searches | Save a filter combination for one-click reuse | apply filters → save search → reuse later |

### 1.4 Per-job panel
| ID | Feature | Function | Flow |
|---|---|---|---|
| J1 | Overview tab | Job details + Application Status field | open job → Overview |
| J2 | Notes | Free-text notes per position | panel notes section |
| J3 | Documents | Application documents kept with the job | panel documents section |
| J4 | Emails tab | Recruiting email threads matched to the application (requires Email Integration) | open job → Emails → browse threads sorted by recency; read marks as read; archive threads |
| J5 | AI Email tab | Generate follow-ups, thank-yous, referral requests using role + company + JD + profile (Simplify+ only) | open job → AI Email → pick stage-grouped template → add personalization → generate |
| J6 | Compose email | Send emails to recruiters from inside the tracker; recipients suggested from thread contacts; compose window minimizable | Emails tab → Compose → recipients → write → Send |
| J7 | AI draft in compose | "Ask AI" in compose window: custom prompt or stage template | Compose → Ask AI → template → generate → edit → send |

### 1.5 AI Email templates (stage-grouped)
| Stage | Templates |
|---|---|
| Application | follow up after applying, ask for a referral |
| Assessment | request deadline extension |
| Interview | thank-you, follow up, reschedule, decline |
| Offer | negotiate, accept, request more time, decline |
| No longer in consideration | final thank-you |

### 1.6 Flow Chart Insights
| ID | Feature | Function | Flow |
|---|---|---|---|
| A1 | Funnel view | Third view toggle: Columns / List / **Flow Chart** | top action row toggle |
| A2 | Funnel metrics | Total applications sent; screens+interviews received; offers; rejections; no-response count | auto-updates as statuses change |
| A3 | Snapshots | Save chart snapshots to track trends over time (response rate, interview volume, conversion) | save → compare over time |
| A4 | Share / export | Public share link (viewable without Simplify account) or PNG export | share → link / export PNG |

### 1.7 Email Integration (Gmail, public beta)
| ID | Feature | Function | Flow |
|---|---|---|---|
| E1 | Gmail connect | OAuth connect in Settings → Email Integration; initial sync matches recruiting emails to tracked apps | settings → enable → Google sign-in → sync |
| E2 | Smart matching | Recruiting emails auto-matched to tracked applications | automatic after sync |
| E3 | Recommended status updates | When an email suggests a stage change (interview invite, rejection), Simplify recommends a status update | recommendation appears → Approve / Decline / Approve All |
| E4 | Disconnect | Disable anytime; reconnect runs fresh sync | settings → disable |

### 1.8 Extension extras (tracker-adjacent)
| ID | Feature | Function |
|---|---|---|
| X1 | Resume keyword comparison | On a posting, Copilot compares resume vs JD: matching keywords, missing skills/terms |
| X2 | Per-role resume tailoring | Generate tailored resume version before applying (Resume Builder) |
| X3 | Unique-question memory | Answers written for unique application questions are saved and reused when the exact question appears again |
| X4 | Multi-resume switch | Multiple saved resumes, switchable in the extension panel before autofill |

### 1.9 Job discovery (tracker-adjacent, NOT in scope for copy)
Job Board, Job Matches, personalized recommendations — Simplify's data moat. Out of scope per prior recon.

---

## 2. your-interview tracker — code-traced inventory

Source: `web/src/app/features/tracker/tracker.component.ts` (2684 lines) + template + dialogs.

### Views & pipeline
- Board (kanban) / List view toggle — sidebar buttons switch views (`setViewMode`, persisted to localStorage)
- Drag-drop between columns with optimistic update + rollback on failure
- Column hide/show (per status)
- Quick status change on card
- Statuses: Saved, Applied, Screen, Interview, Offer, Accepted, Rejected, Ghosted, Paused, Withdrawn
- Sidebar: Status → board, List → list, Overview → scroll to metrics (added 2026-10-18)

### Capture
- Manual create dialog (company, role, location, salary, status, priority, date, link, notes, JD text, outreach message, documents string)
- Smart add dialog (JD paste → parse)
- CSV import (parses rows → bulk create)
- CSV export; JSON backup export + **restore** (added 2026-10-18)
- No browser extension, no auto-logging

### Find & filter
- Search box, status filter chips, stale-only toggle, sort options, pagination
- No saved searches, no date-range filter, no job-type filter

### Per-app detail dialog
- Overview tab: editable notes, JD summary, JD full text, **cover letter generator/editor**, resume-match score tier
- Comms tab: communications log (Email/Phone/LinkedIn/Other) with contact name+email, subject, content, date; add/edit/delete
- Prep-plan dialog: when status → Interview, Material dialog with JD keyword extraction → jump to Playbook `?company=`

### Smart features
- Ghosting detection (21-day stale badge + ghosting list)
- Follow-up flagging (14-day rule for Saved/Applied/Screen, or explicit `nextFollowUpAt`)
- Weekly application goals (set target, progress)
- Answer library dialog (reusable answers inserted into forms)
- Company logo fetch with initials/color fallback

### What it does NOT have (vs Simplify)
- No favorites/stars
- No archive concept (Active/Archived); delete is permanent via native `confirm()`
- No bulk actions at all
- No saved filter searches
- No AI email / follow-up email generation (has comms *log*, no *drafting*)
- No funnel/flow-chart view; metrics are a stat strip only
- No email integration (no Gmail sync, no recommended status updates, no in-app compose)
- No referrals/LinkedIn-profile tracking fields
- No "job type" field/filter
- Documents are a free-text string, not attachments

---

## 3. Gap analysis

| # | Simplify feature | your-interview status | Build cost | Notes |
|---|---|---|---|---|
| G1 | Archive (Active/Archived, Archive-all, Manage Jobs) | ❌ missing | M | Biggest workflow gap. Statuses exist; archive is a soft-state + sub-tab + bulk actions |
| G2 | Favorites (heart) | ❌ missing | S | Trivial: boolean flag + filter |
| G3 | AI Email (stage-grouped templates + personalization) | ❌ missing | M–L | your-interview HAS cover-letter gen; email gen is the same pattern applied to comms. Needs AI key UX |
| G4 | Flow Chart funnel view + snapshots/share | ❌ missing | M | Stat strip exists; funnel = aggregate counts by status over time + PNG export |
| G5 | Saved filter searches | ❌ missing | S | Persist filter presets per user |
| G6 | Email Integration (Gmail sync, recommended status updates, in-app compose) | ❌ missing | L–XL | Needs OAuth + backend job; beta-grade. High value, high cost |
| G7 | Date-range + job-type filters | ❌ missing | S | Additive to existing filter row |
| G8 | Bulk actions (multi-select → status/archive/delete) | ❌ missing | M | No selection model in tracker today |
| G9 | Referrals / LinkedIn contacts per app | ⚠️ partial | S | Comms has contacts; add explicit referral fields |
| G10 | Extension auto-logging | ❌ missing | XL | Out of scope for now (no extension infra) |
| G11 | Resume keyword comparison per posting | ⚠️ partial | M | Playbook has JD gap analysis; not wired into tracker flow |
| G12 | Per-job Documents attachments | ⚠️ partial | M | Free-text `sentDocuments` today; real file attachments need storage |

---

## 4. Priority ranking (for Forrest)

Ranked by: his volume (40+ apps), time pressure, and stated rules (data never lost, real usability).

1. **G1 — Archive** (M). His #1 clutter problem. Rejections pile up in the pipeline; today the only options are permanent delete (scary) or leaving them (noise). Simplify's "archive, don't delete" with Active/Archived tabs is exactly right. Pair with the "Archive all in current view" for end-of-week cleanup.
2. **G8 — Bulk actions** (M). Multi-select → archive/reject/delete in one pass. He processes dozens of applications; one-by-one status changes are the slowest part of his workflow.
3. **G3 — AI Email** (M). He already writes follow-up/thank-you emails by hand. Stage-grouped templates (follow-up, thank-you, reschedule, negotiate) with the job's JD + his profile preloaded = the single highest-leverage AI feature in the tracker. Reuse the existing cover-letter generation pattern.
4. **G2 — Favorites** (S). Cheap, high daily value: star the 3 companies he actually cares about this week.
5. **G5 — Saved searches** (S). "Show me everything in Interview stage" as one click. He re-applies the same filters constantly.
6. **G4 — Flow chart** (M). He wants to see conversion (applied → interview rate). Snapshot-over-time answers "am I getting better at this?" — motivating during a long search.
7. **G7 — Date-range filter** (S). "What did I apply to last week?" Small, useful.
8. **G6 — Email integration** (L–XL). The dream feature (recruiter email → auto status suggestion), but it's OAuth + sync infra + privacy surface. Park until G1–G5 ship.
9. **G9/G12 — Referrals, document attachments** (S–M). Nice-to-have; comms tab covers 80%.
10. **G10/G11 — Extension, keyword compare** (XL/M). Extension is a separate product decision; keyword compare belongs in Playbook first.

### Suggested build order (additive, per repo rules)
**Batch 1 (this week):** G2 favorites → G5 saved searches → G7 date-range filter — all small, all in the tracker frontend, no backend migration.
**Batch 2:** G1 archive (+ Active/Archived tabs, bulk archive) → G8 bulk actions — needs backend `IsArchived` + migration, then frontend.
**Batch 3:** G3 AI email — new dialog + backend AI call, reusing cover-letter infra.
**Later:** G4 funnel, G6 email integration (needs his explicit go-ahead on Gmail OAuth).

---

## 5. Open questions for browser verification (needs live browser)

1. Exact Simplify pipeline stages (Saved / Applied / ? / Offer / ?) — docs imply ~5–6; confirm column names.
2. Per-job panel tabs beyond Overview/Emails/AI Email (Documents? Notes location?).
3. "Manage Jobs" bulk UI: which actions (status? archive? delete? tags?).
4. Flow Chart: is it a Sankey/funnel, and what time windows (all-time / 30d / custom)?
5. AI Email template list exact wording (for clean-room reimplementation of *function*, not copy).
6. CSV import: column mapping UI and duplicate handling.

## Sources
- https://help.simplify.jobs/en/collections/1126456-tracking-your-job-search (4 articles, updated 2026-10-14)
- https://help.simplify.jobs/en/articles/2140179-using-the-job-tracker
- https://help.simplify.jobs/en/articles/7827896-using-ai-email
- https://help.simplify.jobs/en/articles/6486416-flow-chart-insights
- https://help.simplify.jobs/en/articles/0236686-email-integration
- https://chromewebstore.google.com/publisher/simplify-jobs/ua5e47b24fb6adb335148b99574a58244 (extension listing)
- https://simplify.jobs/ (homepage, tracker section)
- https://www.jobscan.co/blog/jobscan-vs-simplify/ (tracker: contacts, interview notes, follow-up email generator)
