# 联机功能优化聚合包 (Co-op QoL Suite) — `v1.2.0`

Zero-dependency, lightweight all-in-one QoL toolbar for **Slay the Spire 2** (Solo & Multiplayer Co-op). In-game UI, chat presets, event logs, stats, and God Mode **automatically follow the game's active language** (Simplified Chinese / English) and switch live when you change language in Settings.

零第三方前置、轻量级集成顶栏 Mod，单人模式与多人联机全程通用，**局内语言自动跟随游戏当前语言（中文 / English）**，可一键收起为单个可拖拽悬浮按钮。

## 核心功能 / Key Features

- **快捷命令与三槽跨层回档 (`Quick` / Hold `F5`)**：一键重打本房（`Shift+点击` 或长按 `F5` 免确认）、自动快照回退上一层、3 个独立持久化存档/回档槽位（统计自动同步回滚）；联机客机可发送【请求 SL】提示房主。
- **上帝模式 · 创造面板 (`F8`)**：可视化修改生命/上限/金币/能量/辉星/格挡/摸牌、升级手牌或牌组、增减任意 Buff/Debuff、检索生成或移除卡牌/遗物/药水；联机房主支持切换操作对象为任意队友。
- **只读队友观战视角 (`Tab`)**：实时查看队友手牌、充能球、牌堆与悬停瞄准高亮，严格只读防误触。
- **大屏飞行弹幕 & 快捷声明 (`Enter` / `G` / `Alt+左键`)**：多泳道防重叠弹幕（兼容中文输入法）、药水弹窗原生样式「声明」按钮、悬停按 `G`（或双击/Alt+左键）一键广播卡牌/遗物/状态；在「弹幕」与「快捷」页签自动展示外部动作拓展包（如 `Sts2ActionPack`）按钮并支持联机同步。
- **四分类时序事件流 (`F7`) & 四维统计 (`F6`)**：
  - 事件流支持大事件 / 小事件 / 伤害状态 / 否定事件（精确区分回合结束未打出弃牌与卡牌效果弃置、记录事件/选牌放弃项）。
  - 统计面板涵盖四大维度：**总伤害**（含被格挡与易伤提供）、**总承伤**（含被格挡/缓冲/无实体/遗物减免、受虚弱影响、死亡与回复）、**总负面**（按每只怪物每层统计易伤、虚弱/缩小、临时与永久降力量）、**总团队**（抓多人牌、使用多人牌、供药水、营火帮队友愈合）。

## 安装方法 / Installation

1. 前往本仓库 [Releases](../../releases) 下载最新的 `CoopQolSuite-v1.2.0.zip`。
2. 解压到《杀戮尖塔 2》安装目录下的 `mods/` 文件夹中，使目录结构为：
   `<Slay the Spire 2>/mods/CoopQolSuite/CoopQolSuite.dll` 与 `CoopQolSuite.json`。
3. 启动游戏即可生效（可在游戏主菜单 Mod 列表中确认已启用）。

## 目录结构 / Directory Structure

- `manifest.json` — Mod 清单（`id: "CoopQolSuite"`, `version: "1.2.0"`, `affects_gameplay: false`）
- `src/` — C# 源码（`Core/`、`Actions/`、`GodMode/`、`QuickSl/`、`Spectate/`、`Stats/`、`Terminal/`）
- `build/` — 独立零前置构建脚本（`build.mjs` + `Shared/`）

## 从源码构建 / Build from Source

需要 **Node.js (v18+)** 与 **Visual Studio 2022 / BuildTools**（含 Roslyn `csc.exe`），以及本地《杀戮尖塔 2》游戏目录（可通过环境变量 `STS2_DIR` 指定）：

```powershell
node build/build.mjs
# 构建并直接部署到游戏目录：
node build/build.mjs --deploy
```
