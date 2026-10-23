# Triage Labels

The skills speak in terms of five canonical triage roles. This file maps those roles to the actual label strings used in this repo's issue tracker.

| Label in mattpocock/skills | Label in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`               | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), use the corresponding label string from this table.

Edit the "Label in our tracker" column to match whatever vocabulary you actually use.

## 本地 Markdown 台账下的落地方式

本仓库用本地 Markdown 而非 GitHub Labels，因此上述角色串不落到 GitHub label 上，
而是写进工单文件的 `Status:` 行（值即角色串），例如 `Status: ready-for-agent`。

另需注意与既有 PROTOCOL 状态机的映射（`docs/fix-program/FIX-STATUS.md` 为准）：
`TODO` / `DOING` / `PUSHED` / `VERIFIED` / `BLOCKED-DECISION` / `BLOCKED-EXTERNAL`。
工程技能的 `claimed` / `resolved` 等本地状态写在各工单文件内，不与 PROTOCOL 状态机冲突。
