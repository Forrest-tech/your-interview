# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`docs/fix-program/PROTOCOL.md`** —— 本仓库最高执行规则，任何工程技能工作前先读它。
- **`docs/fix-program/FIX-STATUS.md`** —— 工单队列与状态机事实源。
- **`GLOSSARY.md`** at the repo root, or
- **`GLOSSARY-MAP.md`** at the repo root if it exists: it points at one `GLOSSARY.md` per context. Read each one relevant to the topic.
- **`docs/adr/`**: read ADRs that touch the area you're about to work in.

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) creates them lazily when terms or decisions actually get resolved.

## 本仓库已存在的领域文档（事实源，优先于新建）

| 文档 | 作用 |
|---|---|
| `docs/fix-program/PROTOCOL.md` | 修复工程执行协议（角色边界、状态机、关票四步、队列顺序） |
| `docs/fix-program/FIX-STATUS.md` | 工单队列/台账（对账三数、队列表、已落地清单） |
| `docs/fix-program/source/AUDIT.md` | 审查单与票面事实源 |
| `docs/fix-program/source/HANDOVER.md` | 交接说明 |
| `web/CLAUDE.md`、`src/` 各服务注释 | 代码内的契约口径注释（后端字段名严格对齐后端 JSON） |
| `BUGFIX-LOG.md`、`ARCHITECTURE.md` | 历史修复记录与架构说明 |

工程技能产出新术语/新决策时，**优先沉淀到上述体系**（如 `docs/fix-program/evidence-<票号>/`），
其次才考虑新建 `GLOSSARY.md` / `docs/adr/`。

## File structure

Single-context repo (most repos):

```
/
├── GLOSSARY.md
├── docs/adr/
│   ├── 0001-event-sourced-orders.md
│   └── 0002-postgres-for-write-model.md
└── src/
```

Multi-context repo (presence of `GLOSSARY-MAP.md` at the root):

```
/
├── GLOSSARY-MAP.md
├── docs/adr/                          ← system-wide decisions
└── src/
    ├── ordering/
    │   ├── GLOSSARY.md
    │   └── docs/adr/                  ← context-specific decisions
    └── billing/
        ├── GLOSSARY.md
        └── docs/adr/
```

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `GLOSSARY.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR (or PROTOCOL/FIX-STATUS 的口径), surface it explicitly rather than silently overriding:

> _Contradicts PROTOCOL §3 (关票四步), but worth reopening because…_
