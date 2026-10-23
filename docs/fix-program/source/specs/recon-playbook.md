# Recon: Playbook — Full Module Requirements
Date: 2026-10-15 | Scope: whole Playbook module (list, entry detail, round workspace, six-dim analysis, guidance, Q&A import)

## Sources
| source | what it gave |
|---|---|
| User's original spec 《Your Interview 平台 —— 完整需求总集 v1.0》 (Openclaw, 2026-09-24; §6.3.3 / §6.3.4 / §6.3.5 / Part 5 six dimensions) | The numbered requirements below — **original doc not found**, reconstructed from user's brief on 2026-10-15; wording below is user's spec as reconstructed |
| User additions 2026-10-07 evening | Per-round full workspace (invite emails, recording+text, pre-interview questions, prep checklist); global calendar view of upcoming interviews; Teams join links; reminders |
| your-interview current implementation (web + src/Analysis.Worker) | What exists now: list page, detail page, AnalysisPipeline/DiagnoseStage (six dims), guidance dialog + versioning, PDF/print export |

## User's requirements (as given)
R01 — One record per interview: upload audio or paste transcript → run analysis → six dimensions + weak points.
R02 — Chain modules: Tracker row "备战" auto-creates a Playbook draft (company/role/JD carried over); Playbook detail has "模拟面试" button jumping into Mock with company/role context.
R03 (§6.3.3) — Multi-round entity: each company has separate rounds; each round has order, stage (Screen/Technical/SystemDesign/Behavioral/Final), time, interviewers, format, location, outcome, notes, feedback.
R04 (§6.3.4) — Guidance material generation + export: manual button trigger (never automatic), versioned; export PDF/Word/images; prompt built from JD + company intro + historical Q&A + six-dim diagnosis.
R05 (§6.3.5) — Import Q&A into TechStack: analyzed Q&A list pre-checked (all selected by default), user can uncheck or edit, imports into TechStack with source tag, linkable back.
R06 (Part 5) — Six-dimension diagnosis display: wpm, average sentence length, too-short sentences, filler words (hesitation vs colloquial counted separately), long pauses, self-repetition, term pronunciation table; off-topic answer comparison; structure-skeleton detection; **only the candidate's own speech is scored**.
R07 (2026-10-07) — Each round is a full workspace: interview invite emails, recording + transcript, pre-interview questions, prep checklist — all inside the round.
R08 (2026-10-07) — Calendar view of upcoming interviews (global), with Teams links and reminders.

## Screen inventory (current)
| ID | screen | route | purpose | key components | states |
|---|---|---|---|---|---|
| S01 | Playbook list | /playbook | entry list | stats cards, company/status filters, entry cards | empty, filled |
| S02 | Entry detail | /playbook/:id | interview detail | tabs (Overview/Rounds/Guidance/Q&A/Assets/Edit), review panel | loading, filled, error |
| S03 | Round workspace | S02 > Rounds tab | per-round detail | prep checklist, emails, recording/transcript, notes/feedback | collapsed, expanded |
| S04 | Review (analysis result) | S02 (analysis panel) | six dims + weak points | dimension scores, weak-point list | not-analyzed, analyzed |
| S05 | Guidance dialog | S02 > Guidance tab | generate guidance | 4-input dialog, version list, export buttons | empty, loading, versioned |

## User flows
```
F01 Create entry: Tracker "备战" -> POST /api/interviews (company/role/JD) -> S02 (Draft)
F02 Analyze: S02 -> upload audio / paste transcript -> run analysis -> S04 (6 dims + weak points)
F03 Guidance: S02 > Guidance -> fill 4 inputs (JD + company intro + Q&A + six-dim diagnosis) -> generate -> versioned -> export PDF/Word/print
F04 Import Q&A: S02 > Q&A tab -> uncheck/edit -> import to TechStack (source tag) -> link back
F05 Mock jump: S02 -> "模拟面试" -> /mock?company=..&role=..
F06 Round workspace: S03 -> add emails / recording+transcript / prep questions / checklist / notes
```

## Components
- entry-card: company, role, status badge (Draft/Analyzed), round + date, six-dim mini scores, "View review", delete
- stats-cards: Total interviews / Analyzed / Pending / Average score / Total weak points
- round-card: stage/order/date/interviewers/format/location/outcome + workspace sections
- analysis-panel: Overall + Pronunciation / Fluency / Structure / Technical Depth / Relevance, weak-point list
- guidance-dialog: 4 inputs, generate (manual), version history, export PDF / Word

## Inferred data model (backend)
```
Interview: id, companyId, company, role, jd, status (Draft/Analyzed), createdAt
InterviewRound: id, entryId, order, stage, scheduledDate/Time, interviewers,
  format, location, outcome, notes, feedback, meetingLink,
  prepQuestionsJson, emailsJson, transcript, recordingUrl
InterviewAnalysis: entryId, overall, pronunciation, fluency, structure,
  technicalDepth, relevance, weakPoints[], speechMetrics(wpm, avgSentenceLen,
  fillerWords{hesitation, colloquial}, longPauses, repetitions, termPronunciation[])
GuidanceMaterial: entryId, versions[], inputs{jd, companyIntro, qa, diagnosis}
```

## Feature matrix
| req | feature | priority | status |
|---|---|---|---|
| R01 | one record per interview + upload/paste + run analysis | must | done |
| R01 | six dimensions + weak points in review | must | done |
| R02 | Tracker "备战" auto-create draft | must | done (commit 8ed2663) |
| R02 | Playbook → Mock jump | must | done |
| R03 | multi-round entity (stage/date/interviewers/format/location/outcome/notes/feedback) | must | done |
| R07 | round workspace: emails, recording+transcript, prep checklist | must | done |
| R07 | round workspace: pre-interview questions (prepQuestionsJson) | must | done |
| R04 | guidance generation: manual trigger, versioned, 4-input prompt | must | done |
| R04 | guidance export: PDF/Word/images | must | partial — PDF = window.print() (user picks Save-as-PDF); Word = HTML blob hack, not real .docx; image export missing |
| R05 | Q&A → TechStack import (default all selected, editable, source tag, link back) | must | done (import-candidates endpoint + dialog; backlink verify pending) |
| R06 | six-dim display: wpm, avg sentence len, short sentences, filler words (hesitation/colloquial split), long pauses, repetition, term pronunciation | must | done (Analysis.Worker: SpeechMetricsCalculator + DiagnoseStage) |
| R06 | **candidate-only speech: NON-COMPLIANT** — DiagnoseStage uses a text-length heuristic (first-half segments ≤12 words → INTERVIEWER, rest → CANDIDATE); breaks when user pastes own answers only or Q&A interleaves throughout | must | TODO (see evaluation 2026-10-15) |
| R06 | off-topic answer comparison | should | partial — ScoreRelevance is length-based, not semantic |
| R08 | global calendar view of upcoming interviews + Teams links | must | TODO (per-entry upcoming panel only; global view was scoped in 2026-10-08 recon) |
| R08 | reminders | should | skip (needs infra; same as 2026-10-08 recon) |

## Out of scope
- Automatic guidance generation (user explicitly: manual button only)
- Push/email reminders (needs background infra)
- Calendar sync (Google/Outlook OAuth)
- Audio/video upload hosting (link or paste only)

## Size: L (module-complete; remaining work is R05, R04-image-export, R08-global-calendar)
Hard parts: none for list/detail/rounds/guidance (patterns exist). Next: verify R05, finish export formats, global calendar view.

## Evaluation 2026-10-15 (code review vs requirements)
**Compliant:** R01 (record→upload/paste→analyze pipeline, 4 stages), R02 (Tracker 备战 + Mock jump), R03 (multi-round entity), R04 generation (manual + versioned + 4-input prompt), R05 import (select/edit/import-candidates), R06 six-dim metrics (wpm, avg sentence len, short sentences, hesitation/colloquial filler split, long pauses, repetition, term pronunciation table), R07 round workspace.

**Non-compliant / partial:**
1. R06 candidate-only speech — text-length heuristic mislabels speakers (see matrix).
   → FIXED 2026-10-15 (branch `rabbit/speaker-label-fix`, commits `5e6a383`+`87f4292`, NOT pushed): new SpeakerLabelStage (Transcribe→Measure) with marker parsing (面试官:/我:/Interviewer:/Me:/候选人:/Candidate:, full/half-width colon, bracket tags, case-insensitive) + inheritance + all-CANDIDATE fallback when no markers; old heuristic deleted from DiagnoseStage; review API + frontend show "计分文本" (scored candidate text). 39/39 unit tests pass (29+10, verified via reflection runner — `dotnet test` vstest/testhost is broken on this VM, env issue). Follow-up `87f4292`: MeasureStage now candidate-only — wpm denominator = candidate speaking time, pauses counted only between adjacent candidate segments, pronunciation assessed on ffmpeg-extracted candidate-only audio with CandidateTranscript reference (200s quota on candidate audio; graceful degrade on failure).
2. R06 off-topic comparison — length-based, not semantic.
3. R06 structure skeleton — rule-based with "骨架证据不足" fallback, no real STAR detection.
4. R04 export — no real PDF/Word/image export.
5. R08 global calendar — not implemented (per-entry upcoming panel only).
6. No entry has ever been analyzed (both Draft, 0 analyzed) — pipeline unproven end-to-end.
