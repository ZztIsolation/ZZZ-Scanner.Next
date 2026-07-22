# Data Sources

本项目按用户要求，以 BiliGame wiki 为驱动盘资料来源：

- 驱动盘图鉴：https://wiki.biligame.com/zzz/%E9%A9%B1%E5%8A%A8%E7%9B%98%E5%9B%BE%E9%89%B4
- 驱动盘介绍与词条：https://wiki.biligame.com/zzz/%E9%A9%B1%E5%8A%A8%E7%9B%98

## 已整理到项目的数据

- `Data/drive_discs.json`
  - `sets`：OCR 名称纠错使用的驱动盘套装名。
  - `extraNameCandidates`：额外候选名，便于快速补充新版本套装，不影响主资料列表。

- `Data/stat_rules.json`
  - `slotMainStats`：I-VI 号位主词条候选。
  - `subStats`：副词条候选。
  - `mainStatValues` / `subStatValues`：S/A/B 品质的合法数值范围。
  - `mainStatAliases` / `subStatAliases`：wiki 文案和游戏内文案之间的别名映射。
  - V 号位的各元素伤害加成使用同档数值成长；新增元素时必须同时补齐候选和 S/A/B 三档数值规则。

- `Data/scan_profiles.json`
  - 背包 UI 点位、颜色、详情区 OCR 裁剪矩形。
  - 坐标以 `standardScreen` 为基准，运行时按窗口客户区比例缩放。

## 维护规则

1. 新驱动盘只改 `Data/drive_discs.json`。
2. 新词条、数值改 `Data/stat_rules.json`。
3. UI 改版或非 16:9 点位适配，新增一个 `scan_profiles.json` profile，不要覆盖旧 profile。
4. 每次改数据或扫描算法，都在 `docs/CHANGELOG.md` 追加记录。

## 备注

wiki 页面可能存在展示组件和 MediaWiki API 内容不同步的情况，所以 `extraNameCandidates` 单独保存，作为 OCR 兜底字典。正式整理完的新套装应移动到 `sets` 并补充 sourcePage。
