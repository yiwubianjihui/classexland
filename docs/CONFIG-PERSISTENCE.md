# 配置持久化

插件配置放在 ClassIsland 配置目录下的 `Plugins/yibianhui.classexland/`。这一页说明**为什么以前会丢配置**、现在怎么保护、以及出问题时怎么救回来。

## 曾经的事故与根因

1.0.0.5–1.0.0.13 期间出现过「每次更新插件，配置就被重置」。排查后确认不是版本升级导致的，而是代码缺陷：

`BellSettings.Load()` 末尾有一句**无条件** `Save()`：

```csharp
finally { _loaded = true; }
Save();          // ← 每次启动都把内存里的值写回磁盘
```

于是每次启动都会拿"刚从文件读出来（或读取失败后剩下的默认值）"回写磁盘。只要读取环节出过任何问题——文件被清空、JSON 写了一半、磁盘暂时不可读——默认值就会**永久覆盖**用户配置。实测证据：宿主 22:17:35 启动，`bell.json` / `bell-options.json` 在 22:17:38 被改写成默认值，而 `duty.json`、`lucky.json` 完全没有被动过（它们走的是「缺失才写」的正常逻辑）。

**1.0.0.14 已修复**：只有文件原本不存在时才落初始模板；读取失败就保留默认值继续运行，绝不覆盖磁盘。

> 顺带说明：每节课的预备铃提前量**真正的数据源是档案**（`Profiles/*.json` 里时间点的 `AttachedObjects`），
> `bell.json` 只是兜底副本。所以 `bell.json` 被清空时，档案里的值仍然有效、铃声照常响。

## 现在的保护（`ConfigSnapshotGuard`）

启动时按顺序做两件事，都在任何配置类被解析**之前**：

1. **修复**：`bell.json`、`bell-options.json`、`duty.json`、`activities.json`、`activity-options.json`、
   `layout.json`、`remote.json`、`lucky.json`、`bridge.json` 中，凡是缺失、空文件、或解析不出 JSON 的，
   用最近的快照补回来；
2. **快照**：给当前内容存一份按天归档的副本到 `Backups/snapshots/yyyyMMdd/`，
   同一天内容没变就不重复写，最多保留 14 天。

整个过程全部 `try/catch`，备份失败绝不影响插件启动。

## 出问题怎么办

| 情况 | 处理 |
|---|---|
| 某个配置变成空/坏 JSON | 下次启动会自动从快照恢复，日志里能看到修复了哪个文件 |
| 想手动回滚到某天 | `Backups/snapshots/<日期>/` 下的文件直接复制回配置目录 |
| 快照也没有 | 从 ClassIsland 自身的 `data/Backups/` 或你的整机备份里找 |
| 升级插件前想保险 | 先复制整个 `Plugins/yibianhui.classexland/` 目录留档 |

## 给开发者的约定

- **加载配置时不要写盘**。只有「文件不存在 → 落初始模板」这一种情况允许写。
- 配置损坏时用默认值继续运行，但**不能**把默认值写回去。
- 新增配置文件时，同时把它加进 `ConfigSnapshotGuard.Protected` 列表。
