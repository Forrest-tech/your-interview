# Recon: Simplify.jobs Tracker → your-interview

## Scope
- **App**: Simplify.jobs job tracker (web)
- **Slice**: Tracker page only — kanban board, job detail, add/import/export
- **For**: your-interview tracker feature parity

## Sources
| source | what it gave |
|---|---|
| alexjiaguo/offerpath docs/simplify-deep-audit.md | 5-col kanban, card design, drawer pattern, counts in headers |
| simplify.jobs/job-application-tracker FAQ | Bookmark → tailor → save docs → interview notes → status updates |
| Competitor clones (annmarya205, delozz, asifahemmed09) | Drag-drop kanban, detail drawer, conversion analytics, CSV |

## Screen Inventory
| ID | screen | purpose | key components |
|---|---|---|---|
| S01 | Tracker board | 5-col kanban | Column headers w/ counts, cards (logo+title+company), quick actions |
| S02 | Job detail drawer | Slides from right, tabbed | Overview / Notes / Documents tabs, status, salary, URL, timeline |
| S03 | List view | Table alternative | Sortable columns, same data |
| S04 | Add application | Manual entry | Company, role, location, salary, URL, notes |
| S05 | Smart add (JD paste) | Auto-parse JD | Paste box → AI parse → pre-filled form |

## User Flows
```
F01 Add via JD paste
    S05 paste JD → auto-parse → review fields → save → S01 (new card in Saved)
    edge: parse fails → manual form fallback

F02 Move through pipeline
    S01 drag card Applied → Interviewing → S02 verify
    edge: invalid transition → revert with toast

F03 Interview prep
    S01 click card → S02 drawer → notes tab → add interview notes
```

## Components
- KanbanCard: logo, title, company, location, status pill, favorite, menu
- DetailDrawer: 480px right slide-in, tabs (Overview/Comms), close on backdrop
- StatusDropdown: 1-click change with optimistic update
- CsvImport: file picker → parse → bulk create

## Inferred Data Model
```
Application  id, companyId, companyName, role, location, salary, status,
             priority, appliedDate, link, notes, jdText, jdSummary,
             resumeScore, outreachMessage, createdAt, updatedAt
             evidence: API model, CSV export columns, dialog fields
             confidence: high
```

## Feature Matrix
See features.csv — priorities: must (kanban, drawer, drag-drop, JD parse, CSV import/export),
should (stale, funnel, weekly goals), could (AI enrich).

## Out of Scope
- Browser extension auto-fill (requires extension infra)
- Simplify's job board / matching (their data moat)
- Resume builder (separate product surface)

## Size: M (done in ~2 sessions)
Hardest: drag-drop with optimistic status updates, drawer CSS, CSV edge cases.
