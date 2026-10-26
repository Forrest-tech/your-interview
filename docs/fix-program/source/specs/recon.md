# Recon: Interview Round Detail + Interview Calendar
Date: 2026-10-08 | Scope: round detail page + global interview calendar (web)

## Sources
| source | what it gave |
|---|---|
| Huntr interview tracker page (huntr.co/product/interview-tracker) | Per-round notes, question bank, scheduling integration, prep tracking, performance reflection; full timeline of dates; contacts + documents per job |
| Teal interview tracker (via SoftwareOne listing + reviews) | Centralized dashboard: dates, formats, rounds, contacts; custom notes per interview; follow-up email templates; AI prep; video recording + live transcription |
| JobHunt Pro (Etsy) | STAR story library tagged by competency; practice mode hiding notes |
| your-interview playbook-detail (own codebase) | Existing: rounds tab, prep checklist, emails, transcript/recording (edit-only), upcoming panel (per-entry, 14d), .ics download |

## Screen inventory
| ID | screen | route | purpose | key components | states |
|---|---|---|---|---|---|
| S01 | Playbook list | /playbook | entry list | search, filters, entry cards | empty, filled |
| S02 | Entry detail | /playbook/:id | interview detail | tabs: Overview/Rounds/Guidance/Q&A/Assets/Edit | loading, filled, error |
| S03 | Round workspace (expanded) | S02 > Rounds tab > expand | per-round detail | prep checklist, emails, recording/transcript, notes/feedback | collapsed, expanded, synthetic-hint, editing |
| S04 | Upcoming panel (NEW) | /playbook top | global calendar view | upcoming rounds across entries, join links, .ics | empty, filled |

## User flows
```
F01 Review a round: S02 -> Rounds tab -> expand round -> see prep/emails/transcript/notes
    edge: synthetic round (no real round yet) -> must convert first
F02 Add transcript: S03 -> recording/transcript section -> paste URL + text -> save
    edge: section hidden when empty (CURRENT BUG - no entry point)
F03 Check upcoming: S01 -> upcoming panel -> join link / .ics download
    edge: no upcoming -> empty state with guidance
F04 Convert synthetic: S03 (synthetic) -> "create real round" -> round created with entry data
```

## Components
- round-card: collapsed header (order/stage/outcome/date/join/edit/delete) + expandable workspace
- ws-section: titled section (icon + h4) for prep / emails / transcript / info
- upcoming-row: date + name + join button + calendar button
- prep-row: checkbox + text + delete
- email-row: subject + meta + snippet + delete

## Inferred data model (already in backend)
```
InterviewRound: id, entryId, order, stage, scheduledDate, scheduledTime,
  interviewers, format, location, outcome, notes, feedback,
  meetingLink, prepQuestionsJson, emailsJson, transcript, recordingUrl
```

## Feature matrix
| feature | area | priority | status |
|---|---|---|---|
| per-round emails (add/remove) | round detail | must | done |
| per-round prep checklist | round detail | must | done |
| per-round recording URL + transcript (inline add) | round detail | must | TODO |
| synthetic -> real round conversion | round detail | must | TODO |
| global upcoming interviews panel | calendar | must | TODO |
| per-round join link + .ics | calendar | must | done (per-entry) |
| follow-up email templates | comms | should | skip (low value) |
| reminders/notifications | calendar | should | skip (needs infra) |

## Out of scope
- Push notifications / email reminders (needs background infra)
- Calendar sync (Google/Outlook OAuth)
- Video recording upload (link only)

## Size: M (frontend-heavy, 1 backend endpoint)
Hard parts: none — all patterns exist in codebase. Next: implement.
