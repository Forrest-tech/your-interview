# Independent Score — 票 123 Design Remediation Plan **R4**

**Score: 10.00 / 10 — PASS (gate: ≥9.9)**
Scorer: independent (did not write or revise the plan). Date: 2026-10-23.
Object scored: `~/workspace/design-remediation/REMEDIATION-PLAN.md` (R4) + `mocks/`.

Two findings must not be conflated:

- **(a) Plan quality — 10.00, PASS.** R4 closes all four R3 deductions, and I
  re-derived every closure firsthand from the tree — usage traces for all five
  shared components, both §C censuses recounted from scratch, the corrected
  pins, and the propagation of the corrected counts through §C, C-1, C-2,
  Appendix B, V.5 and the new V.7. The preserved substance (R-9's geometry
  contract, the radius role-scale, §C's design, the closure mapping, the §11
  projection) is intact and un-stretched, and a fresh whole-plan sweep —
  including a deliberate hunt for any other claim resting on where a component
  renders, the defect class that survived three rounds — found no remaining
  or newly introduced factual defect. The deduction list is empty.
- **(b) Product-target outlook — unchanged and honest, as in R1–R3.** The
  projection (core 9.26 / core+§C 9.37 / full 9.48, range ≈9.3–9.65 straddling
  the user's 9.5 target) recomputes exactly; the plan does not promise the
  crossing and takes no credit for it. This PASS certifies the plan as a
  factual, executable document; whether the implemented product's re-audit
  lands above or below 9.5 remains fresh-judgment variance the plan itself
  discloses. The two findings are, as before, kept separate.

---

## Method

Read in full: REMEDIATION-PLAN.md (R4, all 629 lines incl. Appendices A/B/C/V
with V.7); SCORE-PLAN.md (R1, 9.25), SCORE-PLAN-R2.md (R2, 9.50),
SCORE-PLAN-R3.md (R3, 9.70); re-audit REPORT.md; DESIGN-PLAN.md;
frontend-design SKILL.md; both mock HTML sources. Then verified against the
audited tree (`~/workspace/design-reaudit-20261023/clone`, `git rev-parse HEAD`
= `f2b989d0`, working tree clean) by re-running the usage traces and both
censuses myself — not by re-reading the plan's arithmetic. Sections R4 did not
alter carry the R3 scorer's 66-row Appendix V re-run (62 exact, its 4 failures
being exactly R3-1…R3-4); every claim R4 changed, added, or re-asserted was
re-derived by me firsthand, as itemized below.

## R3-1…R3-4 closures — all four verified closed, firsthand

- **R3-1 (material-tree attribution; transition count) — CLOSED.** Selector
  trace, re-run: `grep -rn '<app-material-tree' web/src/app` returns exactly
  one instantiation — `features/ai-practice/ai-practice.component.html:57` —
  and `MaterialTreeComponent` is imported only by
  `ai-practice.component.ts`. It renders on AI Practice only; Playbook claims
  no part of it. Transition census, recounted from the tree:
  `grep -rn 'transition:' web/src/app web/src/styles.scss` = **56**, split
  tracker 3 · playbook 4 · techstack 3 · auth 1 · layout 6 · dashboard 0 ·
  styles.scss 0 (audited = **17**), material-tree 5, ai-practice 29,
  ai-setting 2, mock 2, analytics 1 — the plan's figures to the unit, and
  17+5+29+2+2+1 = 56. The 17 declarations read verbatim give the duration
  set {110, 120, 130, 150, 160, 180, 200, 220, 400}ms exactly (the s-unit
  declarations at playbook :210/:385/:437, playbook-calendar :70 and
  techstack :383 account for 180/400/150), and C-1's mapping covers all nine
  values with no gap. Playbook's 4 include `playbook-calendar.component.scss
  :70`, so V.7's "Calendar lives in features/playbook" parenthetical holds.
  `grep -rn 'transition:' web/src/app/shared` returns material-tree's five
  lines and nothing else — no other shared component contributes a transition
  to any surface, so 17 is complete by trace, not by folder.
- **R3-2 (outline census; doc-viewer site) — CLOSED.** `grep -rn
  'outline: none' web/src/app` = **15** hits, classified by trace exactly as
  R4 states: audited **4** — `application-detail-dialog.component.ts:990`
  (`.plain-area:focus`), `intake-panel.component.ts:354`,
  `techstack.component.scss:615` (`.blk-editor:focus`), and
  `app/shared/doc-viewer/doc-viewer.component.scss:111`
  (`.doc-editor:focus { outline: none; border-color: #3f51b5; }`); the other
  11 are 9 in AI Practice feature files (ai-practice.component.scss
  :373/1941/2708/2756, category-manager-dialog :182/212/228,
  material-transfer-dialog.scss :185/300) and 2 in material-tree (:100,
  :303). Doc-viewer trace, re-run: `<app-doc-viewer>` instantiates only at
  `techstack.component.html:425` (remaining hits are the component's own doc
  comment) — Tech Stack only. C-2.2's conversion list names the doc-viewer
  site by pin — it is **not** parked on the phase-0 exemption list — and the
  plan's reachability caveat is itself exact: `editable` is
  `input<boolean>(false)` (`doc-viewer.component.ts:40`) and the sole
  instantiation passes no `[editable]`, so the editor does not render today;
  the plan converts the rule anyway and says why. The false R3 sentence
  ("all other hits are in AI Practice") is corrected everywhere it appeared.
- **R3-3 (.fld.sm enumeration) — CLOSED.** `grep 'border-radius: 7px'` over
  `features/playbook/redesign/round/`: `.fld.sm` rules at exactly **four**
  files — `comm-log.component.ts:156`, `materials-list.component.ts:112`,
  `qa-import.component.ts:104`, `round-record.component.ts:229`. Prep-report
  read directly (:236-248): `.fld` carries `border-radius: 8px` at :238;
  `.fld.sm` at :240 sets only padding/font-size (inherits 8px, on-role, no
  change); the 7px rule at :246 is `.toc-item`. R-1.3, Appendix B, and the
  corrected Extra R3-3 row now all describe exactly this.
- **R3-4 (Sankey pins) — CLOSED.** `tracker.component.scss`: :699 =
  `.sankey-node { fill: var(--signal-tint); stroke: var(--signal-soft);
  stroke-width: 1.5; }`; :700 = `.sankey-node.empty`; :705 =
  `.sankey-node.selected { stroke: var(--brand); stroke-width: 3.5 }`; :707
  = `.sankey-fav-badge text`. Appendix V.2 now pins :699 and :705.

## Propagation of the corrected counts — consistent

The corrected figures appear, consistently, in the §C census bullets, C-1.1
(conversion scope 17; material-tree's 5 explicitly out-of-scope as labelled
AI Practice hygiene, still stilled by C-1.2's app-wide reduced-motion
block), C-2.2 (four named conversion sites), C-2's verification
(trace-classified census instead of an exemption), Appendix B (material-tree
† pin reworded to the traced fact; doc-viewer ‡ pin added), V.5 (both census
rows rewritten with the trace classification), and V.7 (the traces
themselves). A stale-number sweep over the full text finds no operative
remnant of 22 transitions, "renders inside Playbook", "exactly 3" outline
sites, 15px / 2.5px, 238px, or a "240px rail" — each survives only inside
Appendix C's fix maps and correction notes, where it belongs. No mock
content needed to change for R3-1…R3-4, and none did.

## Appendix V.7 — traces re-run by the scorer

All five shared components, re-traced: material-tree → AI Practice only;
doc-viewer → Tech Stack only; `app-pdf-content-viewer` → three sites
(playbook-detail.html:149, application-detail-dialog.ts:350,
resume.html:49); ConfirmDialog → template-selector grep 0 hits, class trace
= admin, ai-practice, mock, playbook, techstack, tracker; InputDialog →
0 template hits, class trace = playbook (guidance-input-dialog) + tracker.
`app/shared/` contains exactly the five component directories the method
claims. Sweep items verified: doc-viewer's 2 `:hover` rules (:57, :80) and
pdf-viewer's 1 scrollbar-thumb hover (pdf-content-viewer.component.ts:34)
do render on audited surfaces and sit inside the app-wide 105 count —
`grep -rn ':hover' web/src/app` = 105, `styles.scss` = 2, shared = 9
(doc-viewer 2, pdf-viewer 1, material-tree 6) — attributed nowhere wrongly,
as V.7 records.

## Preserved substance — confirmed intact

- **§9 geometry, quoted verbatim from the tree by me:** `.sp-dot` 16px, 2px
  `#c3c9d4` border, white fill (:287-294); `.sp-stage.done .sp-dot` brand
  fill + `0 0 0 4px rgba(46,58,140,0.14)` (:305-309); `.sp-line` 2px
  `rgba(0,0,0,0.12)`, `.done` `rgba(46,58,140,0.45)` (:310-316); drawer
  timeline `.tl-dot` 20px / 2px `#D4D9E0`, rail 2px `#E3E6EA`, done = brand
  border + 8px brand center dot (application-detail-dialog :712-731). Mocks:
  dashboard band nodes 16px with `.bdot.offer`/`.bdot.stopd` = white fill +
  verdict 2px border + verdict halo (the contract's version, per R2-5), zero
  `→` characters in the mock; playbook mock page grid `280px 1fr` and
  `.railitem.on` = color-mix 8% background + brand border + `0 0 0 1px` ring
  — no `inset` anywhere in it.
- **§C's remaining census:** 6 `@keyframes` declarations / 5 unique names
  (recPulse :874 + :3014, recHalo :885, resIn :1803, apToastIn :2248,
  drawerSlideIn styles.scss:505); `animation:` = 7 app-wide;
  `prefers-reduced-motion` = 0; no `--dur`/`--ease`/`--motion` tokens; the
  global `:focus-visible` rule (styles.scss:458-466) forces radius 4px as
  C-2 states; `:active` = 7 hits (5 AI Practice rules + 1 comment + tracker
  :561); Dashboard's stylesheet has zero `:hover`; the four `avatarColor()`
  copies return 52/52 (tracker :546), 55/55 (playbook :500), `hsl(h 62%
  46%)` (dashboard :184), 52/52 (dialog :1488), declarations at :542/:496/
  :181/:1484 — all exact.
- **Projection:** recomputed — core axes 9.40/9.30/9.45/9.25/8.90 = 46.30/5
  = 9.26; core+§C 46.85/5 = 9.37; full 47.40/5 = 9.48. R4 changes no credit,
  item scope, or estimate; the only scope adjustments are the ones the
  corrected facts force (C-1's set 22 → 17; C-2's list 3 → 4), each noted
  where it lands. Finding (b) above stands.
- **Census layer:** spot-confirmed from `results.json` directly — middot
  joins detail 21 / playbook 4 / dashboard 4 / login 1; bare dashes detail
  12 / techstack 3 / playbook 2 / dashboard 1 / tracker 1; detail radii 7px
  ×11, 12px ×6, 16px ×2, 8px ×22 — V.1 exact.

## Deductions

**None.** Every claim this revision changed or newly asserted was re-derived
from the tree and reproduced; the unchanged bulk carries the R3 scorer's
66-row verification, whose only failures were the four items now fixed and
re-verified above; and the fresh sweep (stale numbers, render-location
claims, mock constants, trilingual copy completeness in Appendix A) found
nothing further to deduct.

## Minor note (no deduction)

- Two of the 17 audited `transition:` declarations carry no easing token at
  all (`playbook-calendar.component.scss:70` and
  `techstack.component.scss:383`, both `transition: background 0.15s;`), so
  §C's "easing uniformly the bare keyword `ease`" is exact as a statement
  about computed behavior — CSS's initial timing function is `ease`, every
  easing token present in the set is the bare keyword, and no other timing
  function exists anywhere in it — rather than as a token count (15 of 17
  declare it explicitly). The R2 and R3 scorers recorded the same claim
  exact on the same tree, and C-1's conversion treats all 17 identically
  and correctly either way. Recorded so the judgment is on the record; it
  does not meet this gate's bar for a deduction.

## Trajectory

R1 9.25 (eight deductions, −0.75) → R2 9.50 (eight, −0.50) → R3 9.70 (four,
−0.30) → **R4 10.00 (none)**. The design substance passed untouched in all
four rounds; what changed is the factual discipline — R4's usage-traced
census (Appendix V.7) is the first round in which every render-location
claim is backed by a re-runnable trace, and it is the round where the
defect class that survived three revisions finally produces no instance.
The plan is cleared for registration as 票 123 and implementation under its
own §12 protocol.

---

**Final: 10.00 / 10 — PASS (≥9.9)**
Deductions: **no deductions**. All four R3 items (material-tree attribution
and the 17-count transition census · the fourth `outline: none` site at
doc-viewer:111 rendering inside Tech Stack, named in C-2's conversion list ·
the four-file `.fld.sm` enumeration with prep-report's actual state · the
Sankey pins at :699/:705) are genuinely closed and re-derived firsthand; the
corrected counts propagate consistently through §C, C-1, C-2, Appendix B,
V.5 and V.7; the preserved substance (The Track's verbatim geometry, the
radius role-scale, §C's design, the closure mapping, and the honestly
sub-9.5 projection of 9.26 / 9.37 / 9.48) is intact. Plan quality and the
product-target outlook remain separate findings: this PASS certifies the
plan, not the 9.5 crossing, which the plan itself declines to promise.
