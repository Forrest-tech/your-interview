# 票 84 — 删分类后条目卡/Topic chip 旧分类名刷新前不更新（票 16 独立复验 F4）

严重度：S4（呈现滞后，刷新即愈，无数据问题）
来源：票 16 独立真人化复验 T6 附带观察（evidence-16/independent-acceptance.md，截图 ind2-t6-after-delete.png）。

## 现象
管理器删除分类成功后、刷新前，已打开的条目卡与 Topic chip 仍显示旧分类名；刷新后变 Uncategorized。

## 修复口径
删除成功回调后同步刷新条目列表的分类呈现（与服务端真值一致）；不变项 = 删除契约、confirm 文案、条目保留行为不动。可与 S4 批次一并处理。
