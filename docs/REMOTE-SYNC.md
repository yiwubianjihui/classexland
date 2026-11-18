# 远程配置同步

远程同步是 ClassExland 的**可选、高风险能力**，默认关闭。它采用"**快照（snapshot）+ 指令队列（command queue）**"两种形态：快照用于日常配置同步，指令用于临时操控，指令与 CLI 共用同一命令层（`ICommandDispatcher`）。

## 安全约定（必须遵守）

- **地址不写进代码**：服务器地址只写在外部配置 `remote.json` 的 `ServerUrl`（或环境变量），插件二进制中不含任何服务器地址。
- **用户自托管**：插件不提供也不依赖任何官方/第三方服务器；你需要自己搭建并维护服务端。
- **仅 HTTPS**：`ServerUrl` 必须以 `https://` 开头，证书校验默认开启；不允许通过配置关闭校验（本地开发需显式、单独的开关）。
- **令牌 + HMAC 双因子**：每个请求携带 `Authorization: Bearer <token>`；消息体附带 HMAC-SHA256 签名，服务端与客户端共享密钥。令牌与密钥通过环境变量或仅当前用户可读的本机文件提供，**不硬编码、不进仓库**。
- **离线可用**：无网络时插件照常本地工作；同步失败只记审计日志并保留本地配置，不影响上课铃与值日提醒。
- **失败回滚**：任何写入前先备份；签名校验失败、版本冲突或中途出错时回滚到上一版本快照。
- **可停用**：`remote.json` 的 `Enabled: false` 或设置页一键关闭；关闭后不再发起任何网络请求。

同步范围（在 `remote.json` 的 `Scope` 中逐项开关）：插件配置（默认开）、ClassIsland 档案（默认关，高风险）、ClassIsland 全局设置（默认关，高风险）。

## 服务端部署约定

服务端只需提供两个只读/可写端点（路径可自定，写进客户端配置即可；下例为约定默认值）：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/snapshot.json` | GET | 返回当前配置快照；支持 `If-None-Match` / `ETag` 条件请求 |
| `/commands.json` | GET | 返回待执行指令队列；支持游标 `cursor` 与 `ETag` |
| `/commands/ack` | POST | 客户端回传已执行指令的游标与执行结果 |

- 版本：每条快照有单调递增的 `version` 与唯一 `etag`；客户端仅当 `etag` 变化时才应用。
- 指令：每条指令有唯一 `id`、递增 `cursor` 与独立签名；客户端按 `cursor` 顺序执行，执行后 ack；重复 id 必须幂等。
- 所有响应体都带签名；客户端在应用前**先验签，再校验 etag/版本，最后才写入**。

## 快照 JSON 格式

```json
{
  "schema": 1,
  "version": 7,
  "etag": "v7-9f2c1a",
  "generatedAt": "2026-11-19T08:00:00Z",
  "payload": {
    "pluginConfig": {
      "bell": { "...": "见 CONFIGURATION.md" },
      "duty": { "...": "..." },
      "activities": { "...": "..." },
      "layout": { "...": "..." }
    },
    "classislandProfile": { "...": "仅当 Scope 开启时包含" },
    "classislandGlobal": { "...": "仅当 Scope 开启时包含" }
  },
  "signature": "base64url(hmac_sha256(secret, canonical({schema,version,etag,generatedAt,payload})))"
}
```

签名输入为规范化 JSON（`sort_keys=True`、无多余空白、UTF-8）的字节；`signature` 字段本身不参与签名。

## 指令队列 JSON 格式

```json
{
  "schema": 1,
  "cursor": 41,
  "etag": "c41-77ab90",
  "items": [
    {
      "id": "cmd-42",
      "cursor": 42,
      "issuedAt": "2026-11-19T08:05:00Z",
      "command": "layout.switch",
      "args": { "to": "class" },
      "apply": true,
      "signature": "base64url(hmac_sha256(secret, canonical(id,cursor,issuedAt,command,args,apply)))"
    }
  ]
}
```

- `command` 与 CLI 命令一一对应（见 [CLI.md](CLI.md)）；`apply: true` 表示这是写操作，需令牌 + 验签通过，并按 CLI 的安全模型记审计。
- 客户端 ack 示例：

```json
{ "cursor": 42, "results": [ { "id": "cmd-42", "ok": true, "message": "ok" } ], "signature": "..." }
```

## 最小客户端示例（Python 伪例）

仅演示格式与校验流程，**不含任何真实服务器地址或密钥**；实际实现由远程同步模块完成。

```python
import os, json, hmac, hashlib, base64, urllib.request

BASE = os.environ["CLEX_SERVER_URL"]        # 用户自托管，必须为 https://
TOKEN = os.environ["CLEX_REMOTE_TOKEN"]
SECRET = os.environ["CLEX_HMAC_SECRET"].encode()

def canonical(obj: dict) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False).encode("utf-8")

def sign(obj: dict) -> str:
    return base64.urlsafe_b64encode(
        hmac.new(SECRET, canonical(obj), hashlib.sha256).digest()
    ).decode().rstrip("=")

def get_json(path: str, headers: dict) -> dict:
    assert BASE.startswith("https://"), "仅允许 HTTPS"
    req = urllib.request.Request(BASE + path, headers={
        "Authorization": f"Bearer {TOKEN}", **headers})
    with urllib.request.urlopen(req, timeout=10) as resp:   # 证书校验默认开启
        return json.load(resp), resp.headers

def fetch_snapshot(current_etag=None):
    headers = {"If-None-Match": current_etag} if current_etag else {}
    data, resp_headers = get_json("/snapshot.json", headers)
    if resp_headers.get("ETag") == current_etag:
        return None                                        # 无变化，直接用本地配置
    signed_fields = {k: data[k]
                     for k in ("schema", "version", "etag", "generatedAt", "payload")}
    assert hmac.compare_digest(sign(signed_fields), data["signature"]), "签名校验失败"
    assert data["etag"] == resp_headers.get("ETag"), "etag 不一致"
    return data                                            # 校验通过后才进入备份→写入流程
```

写入流程（伪代码）：`backup_local()` → `verify_signature()` → `check_etag_version()` → `atomic_write()`；任一步失败则 `rollback()` 并写审计日志。

## 出问题怎么办

- 设置页一键关闭远程同步，或把 `remote.json` 的 `Enabled` 改为 `false`——插件立即回到纯本地模式。
- 用 `cex backup / restore` 回滚到同步前的配置。
- 查看配置目录下审计日志（`audit.log`）中的同步记录，定位是哪次拉取/写入失败。
