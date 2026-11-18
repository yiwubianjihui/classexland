# 从旧插件迁移

ClassExland 定位为 **DutyList / DutyRoster / 试写作业插件的整合替代**。导入后请停用旧插件，但**旧插件的配置文件永远不会被修改或删除**。

## 迁移前必做：备份

1. 完全退出 ClassIsland。
2. 备份 ClassIsland 数据目录下的 `Config/Plugins/`（至少包含下面列出的旧插件配置）与 `Profiles/`。
3. 确认备份可正常打开。迁移是**追加**操作且会生成差异预览，但备份仍是回滚的最终保障。

> 不碰任何个人敏感名单文件；不要把真实学生数据上传或提交到仓库。

## 来源与迁移内容

| 来源插件 | 配置文件（位于 ClassIsland 数据目录 `Config/Plugins/` 下） | 迁移内容 |
|---|---|---|
| DutyList（`dutylist.jimmyxiao`） | `dutylist.jimmyxiao/duty.json` | 批次 → `DutyPlan`；DayConfig → `WeekDays`/`Rotation`；时间段与人员 |
| DutyRoster（`gordon.dutyroster`） | `gordon.dutyroster/*.txt`（若有） | 纯文本时间行 → `Items` |
| 试写作业（`yibianhui.trialhomework`） | `yibianhui.trialhomework/Settings.json` | 名单组 → ActivityModule 的月度轮换活动（内置"试写作业"预设） |
| lrs2187.duty（残留配置） | `lrs2187.duty/Config.json` | 可选；模板 + WorkerDictionary |

轮换计算语义**对齐 DutyList** 的 `RotationStartDate / RotationPeriodDays / SkipDays`：跳过日不消耗"第 N 天"计数，保证导入后行为一致。

## 迁移步骤

1. 打开 ClassExland 设置页 → "导入导出" Tab（或用 CLI，见下）。
2. 选择来源类型，填入/选择旧插件配置文件路径。
3. **预览差异**：导入是追加，不会覆盖现有值日表；遇到同名/冲突项会逐条列出，由你选择保留、合并或跳过。
4. 确认后执行导入，插件写入 `duty.json` / `activities.json` 并自动备份原文件。
5. 用 `cex duty show --date <日期>` 和设置页预览核对轮换批次、人员、时间段是否正确；用 `cex config doctor` 检查一致性。
6. 确认无误后，到 ClassIsland 插件管理页**停用旧插件**（不要删除其配置文件），避免下课提醒重复弹出。
7. 观察一个完整轮换周期；如有问题用"恢复"回滚到导入前备份（`cex backup restore --yes --confirm`）。

## CLI 方式（等价流程）

`cex migrate` 由 `IDutyMigrationService` 实现，可直接从旧配置目录或单个文件导入。写命令默认 `--dry-run`，`migrate` 属高危命令需 `--yes --confirm`。

```bash
# 1) 预览差异——默认 dry-run，不加 --yes 不会写入
cex migrate --from "<旧配置目录或文件>"

# 2) 确认无误后实际执行（自动备份 + 审计 + 二次确认）
cex migrate --from "<旧配置目录或文件>" --yes --confirm

# 3) 核对
cex duty show --date 2026-11-19
cex config doctor
```

若只需单独调整值日表，可用 `cex duty import`（同样默认 `--dry-run`、需 `--yes`）。任何写操作都会被记入审计日志。

## 停用旧插件

导入完成后，在 ClassIsland 插件管理页依次停用：DutyList、DutyRoster、试写作业插件（以及不再需要的 lrs2187.duty 相关插件）。

- 停用而非卸载/删除，保留原始配置作为回滚依据。
- 若发现提醒缺失，先确认 ClassExland 已启用、`duty.json` 中对应 `Enabled` 为 true，再考虑恢复旧插件。
- 正式上课/下课提醒由 ClassExland 的"宿主提醒一键配置"统一管理；如出现双响，运行 `cex config doctor` 并按提示回滚宿主提醒设置。
