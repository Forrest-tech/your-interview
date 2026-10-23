# 08: COD-06+COD-07 转写契约对齐（字段名与转写入口）

**审计编号：** COD-06, COD-07（严重度 S2）
**区域：** Playbook 前端 + Interviews 契约
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

①getTranscript 前后端字段对齐（前端按 transcriptText 读、后端返 fullText）——在 playbook-v2.api 层统一适配，消费方 round-record/materials-list 拿到真实正文。②startTranscription 改调后端真实存在的条目级 POST {id}/transcription/start（先核该入口语义是否覆盖资产级场景，不覆盖则在后端补资产级路由，二选一并在票内注明）。证据：AUDIT.md COD-06、COD-07。

## Acceptance criteria

- [ ] 有转写数据的条目：详情页实录正文非空渲染（沙盒真 UI 或 API+组件双证）
- [ ] 点「开始转写」不再 404（请求落到真实路由、返回非 404）
- [ ] tsc 通过；Interviews 后端如有改动 dotnet test 全绿

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
