# 远程同步协议 v2 —— 全配置覆盖（服务端/客户端唯一契约）

> 状态：**已定稿**。服务端（server-dev）与客户端（client-dev）必须严格按本文实现。
> 任何字段增删必须先改本文再改代码。2026-11-28 13:05。

## 1. 设计原则

1. **原始文本透传**：所有配置内容以「完整 JSON 文本」传输，服务端不做 decode/encode，
   保住宿主配置的 PascalCase 大小写（见 `lib.php:10-11`）。
2. **命名空间分区**：`snapshot.pluginConfigs` 放插件配置（单层文件名），
   `snapshot.core` 放 ClassIsland 核心配置（Settings.json / Profiles / ComponentLayouts）。
3. **baseHash 冲突检测**：core 每个条目带生成时的内容哈希，客户端写入前比对本地哈希，
   不一致保留本地、不覆盖（diff 备份仍生成）。
4. **敏感字段过滤**：服务端与客户端**两端都做**；命中即整份拒绝，不部分应用。
5. **向后兼容**：老客户端（1.0.0.17）只认四个插件文件、不认 core；
   新服务端下发 core 时老客户端忽略该字段，不报错。新客户端连老服务端时 core 为 null，走旧逻辑。

## 2. 数据结构

### 2.1 manifest 响应（GET `manifest.php?device=<名>`）

```jsonc
{
  "device": "志成十九",
  "head": "<tree hash: 所有纳入文件的 name->sha256 再 hash>",
  "uploadPath": "/remote/upload.php",
  "version": "20261128-130000-a1b2",
  "etag": "<12 hex: 含 head 的清单指纹>",
  "snapshot": {
    "pluginConfigs": {
      "bell.json": "<原始 JSON 文本>",
      "duty.json": "<原始 JSON 文本>",
      "activities.json": "<原始 JSON 文本>",
      "layout.json": "<原始 JSON 文本>",
      "bell-options.json": "<原始 JSON 文本>",   // 新增
      "lucky.json": "<原始 JSON 文本>"            // 新增
    },
    "core": {
      "settingsJson": "<Settings.json 原始文本>",
      "settingsBaseHash": "<sha256 原文>",
      "profiles": {
        "YBH.json": { "json": "<原始文本>", "baseHash": "<sha256>" }
      },
      "layouts": {                                  // 新增：ComponentLayouts
        "Default.json": { "json": "<原始文本>", "baseHash": "<sha256>" },
        "offclass.json": { "json": "<原始文本>", "baseHash": "<sha256>" }
      }
    }
  },
  "commands": [ { "id": "...", "command": "...", "args": {}, "apply": true, "dryRun": false } ]
}
```

### 2.2 上传请求（POST `upload.php?device=<名>`）

```jsonc
{
  "base": "<上次 manifest 的 head>",
  "device": "志成十九",
  "message": "自动上传",
  "files": { "<pluginConfig key>": "<原始 JSON 文本>" },
  "core": {                                  // 与 manifest 的 core 同结构
    "settingsJson": "<文本>",
    "settingsBaseHash": "<本地当前 Settings.json 的 sha256>",
    "profiles": { "YBH.json": { "json": "<文本>", "baseHash": "<本地当前档案 sha256>" } },
    "layouts": { "Default.json": { "json": "<文本>", "baseHash": "<sha256>" } }
  }
}
```

**baseHash 语义（关键）**：上传时 `baseHash` = **客户端本地当前磁盘上该文件**的 SHA-256，
即「我这份内容是从这个基准改来的」。服务端收到后若与服务器当前内容哈希不同 ⇒ 判冲突入待审；
相同 ⇒ 快进。下载时 manifest 的 `baseHash` = **服务端生成这份快照时**的基准哈希，
客户端写入前比对本地，一致才覆盖。

### 2.3 纳入范围（白名单）

**插件配置（6 个，单层文件名）**：`bell.json`、`duty.json`、`activities.json`、`layout.json`、
`bell-options.json`、`lucky.json`。

**核心配置**：
- `settingsJson` → `<data>/Settings.json`
- `profiles.<name>` → `<data>/Profiles/<name>`（当前仅 `YBH.json`）
- `layouts.<name>` → `<data>/Config/ComponentLayouts/<name>`（`Default.json`、`offclass.json`）

**禁止（两端一致）**：`remote.json`、`remote-state.json`、`remote.disabled`、`bridge.json`、
`Credentials.json`、`Policy.json`、任何 `*.bak*`、`Backups/**`、`Cache/**`、`Logs/**`、
匹配 `Debug*`/`Update*`/`Plugins.json`/`EnabledPlugins*`/`*PluginState*`/`ThemePrivate*`；
JSON 键命中 `credential/password/token/secret/apikey/privatekey/policy/debug/update/pluginenabled/
enabledplugins/ispluginenabled/themeprivate/privatetheme` 子串即拒绝。
客户端复用 `CoreConfigPolicy`；服务端在 `lib.php` 新增等价的 `cx_is_forbidden_key()` 递归扫描。

## 3. head / etag / 版本

1. `head = sha256(json_encode(排序后的 name->sha256(原文)))`。
   命名空间键：`pluginConfigs.<name>`、`core.settingsJson`、`core.profiles.<name>`、`core.layouts.<name>`。
   老服务端只算四个插件文件；两端各自按自己支持的集合算，**用 manifest 回传的 head 作 base**，不自行推导。
2. `etag = substr(sha256(完整 manifest 原文), 0, 12)`，必须包含 head（修 S4）。
3. `version` 仍由 `cx_publish_files()` 生成 `Ymd-His-xxxx`；客户端「与上次记录不同即应用」，
   不比较大小（回滚版本也能下发）。

## 4. 冲突与覆盖判定（Q1 已确认）

| 条件 | 行为 |
| --- | --- |
| `LocalDirty=true`（本地哈希 ≠ 基线） | **不覆盖**；先生成 `Backups/changes/<ts>/<name>` 即时备份，再上传 |
| `LocalDirty=false` 且 manifest 版本与本机不同 | 生成 `Backups/diffs/<version>-<name>.diff`，再覆盖；diff 失败则中止 |
| core 条目 `baseHash` 与本地不符 | 该项保留本地 + 记 `ConflictKeptLocal`，其余项照常应用 |
| 上传 409 进待审 | `SaveConflict` 把本次哈希记为基线 ⇒ 本地转净 ⇒ 之后服务器新版本可覆盖 |
| 任一禁止项命中 | 整份拒绝，写审计 |

## 5. diff 切块（服务端）

服务端 `cx_text_diff()` 保持逐行 LCS，但对单行大 JSON 增加「按顶层键切块」：
把 `Settings.json` / `YBH.json` 解析成对象，每个顶层键单独一行序列化后再 diff，
避免整行替换产生的巨型 diff。解析失败降级为原文 diff。

## 6. 两端实现边界

- **服务端负责**：白名单、存储、版本库、diff、待审、管理页审理与合并、敏感键过滤、HMAC 归一化。
- **客户端负责**：本地哈希判脏、即时备份、diff 备份、core 应用与 baseHash 检测、热重载、上传携带 core。
- **管理页负责**：显示每个设备的 pluginConfigs + core 的可编辑文本域、pending diff、采用/丢弃/回滚。

## 7. 兼容性矩阵

| 客户端 \ 服务端 | 老服务端（4 文件） | 新服务端（v2） |
| --- | --- | --- |
| 老客户端 1.0.0.17 | 现状 | 忽略 core，插件文件照常；head 以服务端回传为准 |
| 新客户端 1.0.0.18 | core 为 null，走旧逻辑；插件文件按新版名单上传会被老服务端拒（**故需先部署服务端**） | 全功能 |

> 因此部署顺序固定：**服务端先上线 → 客户端发版 → 源包/bat 更新**。用户已确认「先本地改完一起部署」，
> 指一次性部署而非穿插验证；实际部署动作仍按本矩阵顺序在同一维护窗口内连续完成。
