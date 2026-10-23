# 票43 ARC-13 · 验证记录(VERIFICATION)

**实现 sha**:见 FIX-STATUS.md 票 43 行(推送后回填)
**证据脚本**:`i18n-parity-check.js`(parity+boundary)、`referenced-keys-audit.js`(引用键审计),Node 直跑,可复现。

---

## 一、关票四步(协议 §3)

| 步骤 | 结果 | 证据 |
|---|---|---|
| 1 实现 | 本地字典 498 键并入核心 DICT 命名空间分区;两本地文件下线;makeV2T/makeBriefT/F6 改委托 i18n.t;组件调用点零改动 | git diff;`find web/src -name "*.i18n.ts"` = 0 文件 |
| 2 测试 | parity+boundary **42/42 PASS**;引用键审计 **570 引用 / 0 缺失**;`tsc --noEmit` **0 错**;`ng build`(AOT 模板检查)**exit 0** | 本文件第三节命令与输出 |
| 3 活体证据 | 沙盒无浏览器(仓内先例:票13/35/37 同口径),以机械检查为据:DICT 真实编译执行 + 全键三语逐字比对 + 全前端引用键审计 + AOT 构建 | 第三节 |
| 4 远端确认 | push 后 `git ls-remote` 确认 sha 在 develop-new-features;台账登记 | FIX-STATUS.md |

## 二、对照表(审计条目期望行为 + 改前形态 → 成果)

| # | 设计依据(审计 ARC-13 期望 / 改前形态) | 成果 | 打勾 |
|---|---|---|---|
| 1 | 本地字典文件下线(v2.i18n / brief.i18n) | 两文件删除,`web/src` 零 `.i18n.ts`;引用 grep 仅剩注释性文字 | ✅ |
| 2 | 文案机制只剩一套(文件清单双证) | 唯一字典 = core/i18n/i18n.service.ts DICT;所有 t() 走 I18nService 一条链 | ✅ |
| 3 | 键名保持兼容映射 | 577 本地键名逐字保留(pb.v2.* / pb.br.*),无重命名/并键/删键;parity 测试逐键断言存在 | ✅ |
| 4 | 不丢任何现行文案(三语) | 并入键 zh/en/fr 逐字承袭(测试逐字断言);缺 fr 的 27 键填 en 同文 = 改前 fr 模式实际展示(en 兜底) | ✅ |
| 5 | 组件不再同时引两套 | 10 组件 + prep-plan-dialog + playbook.component 的本地表 import/优先逻辑全部移除 | ✅ |
| 6 | 三语走查无键丢失 | 引用键审计:570 个引用(静态+动态受控域展开)0 缺失 | ✅ |
| 7 | 三语走查无原始键直出 | parity 测试 B4:本地 577 键 × 三语 × 两接线,新机制 0 处直出 key(旧机制对跨命名空间键有直出,生产不可达,已单列说明) | ✅ |
| 8 | tsc 通过(票面验收) | `tsc --noEmit` exit 0 | ✅ |
| 9 | 既有核心条目不受污染 | 测试:基线 DICT 全部键 × 三语逐字零改动;79 重叠键保留核心侧条目 | ✅ |

**偏差清单**(非空,逐条注明原因与批准状态):

| # | 偏差 | 原因 | 批准状态 |
|---|---|---|---|
| 1 | angular.json 初始 bundle maximumError 1mb → 1.2mb | 票面 mandate 将字典并入随 shell 即时加载的核心 service,初始 bundle 1010.09→1096.39 KiB,超原预算 72.39 KiB 致 ng build exit 1(CI 红灯)。按协议 §1(票面 > 现有代码)最低幅度调整 | 已登记 FIX-STATUS 待确认区,**待 Forrest 知悉/裁决**;如不认可,回退该行即可(合并本体不受影响) |
| 2 | 27 个本地独有键 fr 填 en 同文(而非新造法语译文) | 核心 DICT 既有约定:重叠 79 个 pb.br 键 fr 与 en 全部同文;且改前 fr 模式展示即 en 兜底,填 en 同文保持行为逐字一致,避免未批准的口径变更 | 依据仓内既有约定(7b315ce 等),无需批准;如需真法语译文可另票补 |

## 三、验证命令与关键输出

```
$ node docs/fix-program/evidence-43/i18n-parity-check.js
票43 ARC-13 parity+boundary: PASS 42 / FAIL 0
全部通过:三语输出逐字 parity、键集只增不减、既有条目零改动、boundary 各类别覆盖。

$ node docs/fix-program/evidence-43/referenced-keys-audit.js
引用键审计:静态+动态展开共校验 570 个键引用,缺失 0 个
DICT pb.v2.* = 465,pb.br.* = 128;本票并入 498 键,既有重叠 79 键保留核心侧条目
PASS:全前端 pb.* i18n 引用键在核心 DICT 全部有定义 —— 无键丢失、无原始键直出面。

$ cd web && npx tsc --noEmit        # exit 0
$ npx ng build                      # exit 0(Initial total 1.07 MB;AOT 模板检查通过)
$ find web/src -name "*.i18n.ts" | wc -l   # 0
$ grep -rn "V2_I18N\|BRIEF_I18N" web/src --include=*.ts --include=*.html   # 仅注释提及,零代码引用
```

### boundary 类别覆盖(附则 §7,命名标注)

| 类别 | 用例 | 结果 |
|---|---|---|
| 空/null | key = '' / null / undefined × 三语,两侧 parity | ✅ |
| 未知键 | 4 个不存在键 × 三语 × 两接线,原样直出且 parity | ✅ |
| 空集合 | 空 DICT 桩查找;DICT 无重复键;两本地表键域无交集 | ✅ |
| Unicode/emoji | 📋 📝 × – ’ 等值键 parity(按各自键域走对应接线) | ✅ |
| 占位符 | fmt 缺参({n} 原样保留)/ 多参(无关参数忽略)/ 参数含 HTML 特殊字符(原样替换不转义,同旧口径) | ✅ |
| fr=en 补齐 | 27 键 fr 值=en 值,且 fr 模式输出 === 改前(en 兜底)输出 | ✅ |
| 重叠键 | 79 键 zh/en 与本地表逐字同、核心侧零改动 | ✅ |
| 键集完整性 | 本地 577 键全部在册;旧键零丢失;净增 498;无重复 | ✅ |

### 旧机制缺陷态说明(单列,非本票引入)

旧 `makeBriefT` 对 `pb.v2.*` 键(不在 BRIEF_I18N)会回退 `i18n.t` 再落空 → **原样直出 key**;旧 `makeV2T` 对 `pb.br.*` 本地独有键同理。生产调用点键域已 grep 双证无跨命名空间引用(makeBriefT 唯一消费方 prep-plan-dialog 仅用 pb.br.prep.*;redesign 组件零 pb.br.*),故该缺陷态不可达。收敛后新机制对全部键正确解析 —— 与验收口径「无原始键直出」一致,方向为修复而非回退。

## 四、影响面 / 不变项 复述打勾(变更安全协议)

**影响面**(改动项 → 下游 → 流程):

| # | 改动 | 下游/流程 | 状态 |
|---|---|---|---|
| 1 | DICT +498 键(只增不改) | 全站 t()/tn()/tf();全部页面三语 | ✅ 已完成,既有条目零改动(测试断言) |
| 2 | makeV2T/makeV2Fmt 改委托 | 10 组件:playbook-detail、round-card、materials-list、comm-log、round-record、qa-import、prep-report、intake-panel、export-dialog、dashboard | ✅ 调用点零改动,parity 锁定 |
| 3 | makeBriefT 改委托 | prep-plan-dialog(AI 生成/未配置/失败三态) | ✅ 同上 |
| 4 | playbook.component F6 内联 t 去本地表 | Playbook 列表页技术覆盖图谱 | ✅ parity 锁定(8 键 × 三语) |
| 5 | 删除两本地文件 | 仅上述 3 个文件引用(grep 双证) | ✅ 已删,零残留引用 |

**不变项**(逐条打勾):

| # | 不变项 | 状态 |
|---|---|---|
| 1 | 577 键名逐字保留 | ✅ parity 测试断言 |
| 2 | zh/en 文案逐字承袭 | ✅ 测试逐字断言 |
| 3 | fr:有则逐字;27 缺项按 en 同文(展示同改前) | ✅ 测试断言 |
| 4 | 核心 DICT 既有条目(含 79 重叠键核心侧)零改动 | ✅ 测试断言 |
| 5 | I18nService 公共 API(t/tn/tf/setLang/lang/locale/持久化)不动 | ✅ diff 仅 DICT 数据区 |
| 6 | 组件调用点用法零改动 | ✅ diff 仅 3 文件接线 |
| 7 | helpers 其余纯函数不动 | ✅ diff 未触及 |
| 8 | 后端/数据库/迁移零改动 | ✅ 纯前端 |
| 9 | 语言切换/存储键 yi.lang/默认 en 不动 | ✅ 未触及 |
| 10 | angular.json 仅初始预算一行(偏差 1,已登记) | ⚠️ 见偏差清单 |
