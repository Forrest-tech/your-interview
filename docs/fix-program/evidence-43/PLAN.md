# 票43 ARC-13 · 执行计划(PLAN)

**票面**:`docs/fix-program/tickets/43-arc-13-i18n-consolidation.md`(工作票面,含动手前写入的影响面/不变项两张清单)
**审计条目**:AUDIT.md ARC-13(S3)——「三语文案 5 套并行机制共 3235 行,组件常同时引两套;本地字典并入核心 I18nService 命名空间分区,机制收敛为一套」

## 现状(机械对账,非推断)

| 项 | 数 |
|---|---|
| 本地字典文件(analysis/qa 已由票41 下线) | v2.i18n.ts(465 键 pb.v2.*)/ brief.i18n.ts(112 键 pb.br.*) |
| 与核心 DICT 重叠键 | 79(全 pb.br.*;zh/en 逐字相同,核心侧 fr 与 en 同文 79/79) |
| 本地独有需并入 | 498(pb.v2.* 465 三语齐 + pb.br.* 33,其中 27 缺 fr) |
| 接线消费方 | makeV2T/makeV2Fmt → 10 组件;makeBriefT → prep-plan-dialog;BRIEF_I18N 直引 → playbook.component F6 内联 t |

## 执行步骤

1. **并表**:498 个本地独有键按命名空间分区写入核心 DICT(i18n.service.ts),zh/en/fr 逐字承自本地表;缺 fr 的 27 键按核心 DICT 既有约定(重叠 79 键 fr 全部与 en 同文)填 en 同文 —— fr 展示与改前(en 兜底)逐字一致,不新造译文。
2. **接线收口**:makeV2T / makeBriefT / F6 内联 t 去掉本地表优先逻辑,改为直接委托 i18n.t;删除 v2.i18n.ts / brief.i18n.ts;组件调用点零改动。
3. **验证**:
   - parity+boundary 测试(证据 i18n-parity-check.js):镜像源码树(old=基线 f46b30a 双机制 / new=工作树单机制),真实编译执行两侧 helpers,全部键 × 三语断言 oldT === newT;boundary 类别:空/null key、未知键、空集合、Unicode/emoji、占位符(缺参/多参/HTML 特殊字符)、fr=en 27 键、重叠 79 键。
   - 引用键审计(证据 referenced-keys-audit.js):全前端 ts+html 的 pb.* 引用键(静态 + 动态前缀受控域展开)全部在核心 DICT 有定义。
   - `npx tsc --noEmit` 0 错;`npx ng build`(AOT 含模板检查)exit 0。
4. **收尾**:台账置 DOING → 实现完成 push 后置 PUSHED(sha + 测试数字 + 证据指针),待 CI 全绿转 VERIFIED(协议 §3 第四步)。

## 已知后果(需协调者知悉,已登记台账待确认区)

初始 bundle 由 1010.09 KiB 升至 1096.39 KiB(+86.3 KiB):playbook 字典数据从懒加载 chunk 移入随 shell 导航即时加载的核心 service(票面 mandate 的必然结果;应用总包体基本不变,仅懒加载→即时加载再分配)。超过 angular.json 初始 bundle 1mb 错误预算 72.39 KiB,`ng build` exit 1 会红灯 CI。按协议 §1(票面 > 现有代码)将 maximumError 最低幅度调至 1.2mb 并在台账登记,待 Forrest 知悉/裁决。

## 执行线 pivot(2026-10-23,复验前记录)

本线同构实现(分支 `ticket43-my-impl`,`7baa40b`)推送前,发现并行执行线已落地同一票(`92aa138`/`239412f`,基线 `f594b28`,两线独立测量同报 +86kB 量级、同作 1.2mb 结论)。处置:本线实现保留在 `ticket43-my-impl` 不推;本票客体改为并行线实现,本线工具复验(基线改取 `f594b28`,在远端 tip `6dd2304` 工作树执行)。上表「现状」数字对两线基线均成立(本地两表与 helpers 在 `f46b30a..f594b28` 区间零改动,机械核实);核心 DICT 基数差异(1870 vs 1856 量级)来自票47/50 的既有新增,属并行线其他票成果。
