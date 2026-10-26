# 票 85 — InvitePasteParser 英文日期在 invariant 宿主抛 CultureNotFound（源：票 28 特征测试发现）

## 事实（票 28 双向断言锁定，evidence-28/REPORT.md）
InvitePasteParser 英文日期分支调用 GetCultureInfo("en-US")；在开
invariant globalization 的宿主（**生产 runtimeconfig 即此形态**）抛
CultureNotFoundException，英文日期解析整支失效；xunit console 宿主
则正常——宿主分裂，测试绿不等于生产可用。

## 任务
- 改为不依赖宿主 culture 的解析（显式格式 + InvariantCulture 解析，
  或 TryParseExact 格式表），英文日期各形态行为与现测试锁定口径一致。
- 盘点同文件/同域其他 GetCultureInfo / culture 敏感调用（DateTime.Parse
  无 culture 参数等），清单入报告；同型一并修（范围以解析域为限）。

## 验收
- 在 invariant 模式下跑解析测试全过（以环境变量
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 复跑相关套件留证）。
- 既有特征测试（票 28 锁定口径）零回退。
- §7：新增/调整用例含 boundary（闰年 2/29、月日序歧义形态按现口径、
  非法日期字符串）。
