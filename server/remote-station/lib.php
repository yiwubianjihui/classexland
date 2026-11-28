<?php
declare(strict_types=1);

/**
 * ClassExland 远程站 —— 公共库。
 *
 * 设计要点：
 *  1. 所有状态都存成 PHP 文件（data/config.php、data/state.php），
 *     直接访问只会执行、不输出任何内容，因此不需要改 nginx 的 deny 规则。
 *  2. 插件配置以「原始 JSON 文本」保存，发布时原样嵌入快照，
 *     避免 PHP 重新编码把 PascalCase 键名改成 camelCase（插件读不出来）。
 *  3. 令牌比较一律 hash_equals；HMAC 对「响应原文」计算，放在 X-ClassExland-Signature 头里，
 *     不做任何规范化，服务端和客户端算的是同一串字节。
 */

const CX_DATA_DIR     = __DIR__ . '/data';
const CX_CONFIG_FILE  = CX_DATA_DIR . '/config.php';
const CX_STATE_FILE   = CX_DATA_DIR . '/state.php';

/** 插件配置白名单（单层文件名，与 PROTOCOL-CORE-SYNC.md 2.3 一致）。 */
const CX_ALLOWED_PLUGINS = [
    'bell.json', 'duty.json', 'activities.json', 'layout.json',
    'bell-options.json', 'lucky.json',
];
/** 兼容旧代码的别名。 */
const CX_ALLOWED_KEYS = CX_ALLOWED_PLUGINS;

/** 核心配置：结构键 + 摊平到 tree/head 时用的命名空间键。 */
const CX_CORE_SETTINGS          = 'settingsJson';
const CX_CORE_PROFILES          = 'profiles';
const CX_CORE_LAYOUTS           = 'layouts';
const CX_CORE_SETTINGS_TREE_KEY = 'core.settingsJson';
const CX_CORE_PROFILES_PREFIX   = 'core.profiles.';
const CX_CORE_LAYOUTS_PREFIX    = 'core.layouts.';

const CX_MAX_BODY     = 2097152; // 2 MB：全量配置约 210 KB，留足余量
const CX_MAX_DEVICES  = 32;      // 一个人身兼多个班级也够用了

/** 禁止下发/写入的文件名片段（大小写不敏感的子串匹配，覆盖 PROTOCOL 2.3）。 */
const CX_FORBIDDEN_NAME_FRAGMENTS = [
    'remote.json', 'remote-state.json', 'remote.disabled', 'bridge.json',
    'credentials.json', 'policy.json', 'pluginsettings.json',
    '.bak', 'backups/', 'cache/', 'logs/',
    'debug', 'update', 'enabledplugins', 'themeprivate', 'pluginstate',
];
/** 禁止出现的 JSON 键名片段（递归、大小写不敏感的子串匹配）。 */
const CX_FORBIDDEN_KEY_FRAGMENTS = [
    'credential', 'password', 'token', 'secret', 'apikey', 'privatekey',
    'policy', 'debug', 'update',
    'pluginenabled', 'enabledplugins', 'ispluginenabled',
    'themeprivate', 'privatetheme',
];

function cx_boot(): void
{
    if (!is_dir(CX_DATA_DIR)) {
        @mkdir(CX_DATA_DIR, 0750, true);
    }
    $guard = CX_DATA_DIR . '/index.php';
    if (!file_exists($guard)) {
        @file_put_contents($guard, "<?php http_response_code(404);\n");
    }
}

function cx_write_atomic(string $path, string $content): bool
{
    $tmp = $path . '.tmp';
    if (@file_put_contents($tmp, $content, LOCK_EX) === false) {
        return false;
    }
    @chmod($tmp, 0640);
    return @rename($tmp, $path);
}

/**
 * 读-改-写互斥锁。
 *
 * 锁单独的 .lock 文件而不是数据文件本身：cx_write_atomic() 用 rename 替换 inode，
 * 锁住旧 inode 是防不住并发覆盖的。exit（cx_json_out / cx_redirect）不会执行 finally，
 * 所以用 shutdown 回调兜底释放。
 */
function cx_lock_store(string $path): void
{
    static $registered = false;
    $fp = @fopen($path . '.lock', 'c');
    if ($fp === false) {
        return; // 锁文件建不出来时降级，不让整个请求失败
    }
    if (!flock($fp, LOCK_EX)) {
        fclose($fp);
        return;
    }
    if (!isset($GLOBALS['cx_store_locks'])) {
        $GLOBALS['cx_store_locks'] = [];
    }
    $GLOBALS['cx_store_locks'][] = $fp;
    if (!$registered) {
        $registered = true;
        register_shutdown_function('cx_unlock_stores');
    }
}

function cx_unlock_stores(): void
{
    foreach ($GLOBALS['cx_store_locks'] ?? [] as $fp) {
        @flock($fp, LOCK_UN);
        @fclose($fp);
    }
    $GLOBALS['cx_store_locks'] = [];
}

/**
 * 读写状态文件。
 *
 * 刻意不用 `include`：宝塔的 opcache 对「时间戳校验」可能关闭，
 * 写回新文件后 include 仍然返回旧内容（实测会把刚发布的版本覆盖回空状态）。
 * 这里用 `<?php exit; ?>` + JSON 的形式，读取走 file_get_contents，
 * 直接访问时 PHP 执行 exit，不会输出任何内容。
 *
 * $strict = true 时，「文件存在但读不出来／解析不了」会抛异常，
 * 而不是返回 null —— 否则上层会当成「还没初始化」，静默用空状态覆盖掉真实数据
 * （root 跑过一次 CLI 把文件属主改成 root、php-fpm 读不到，就踩过这个）。
 */
function cx_read_store(string $path, bool $strict = false): ?array
{
    if (!file_exists($path)) {
        return null;
    }
    $raw = @file_get_contents($path);
    if ($raw === false) {
        if ($strict) {
            throw new RuntimeException('无法读取 ' . basename($path) . '（检查文件属主与权限）');
        }
        return null;
    }

    $pos = strpos($raw, "\n");
    if ($pos !== false) {
        $data = json_decode(substr($raw, $pos + 1), true);
        if (is_array($data)) {
            return $data;
        }
        if ($strict) {
            throw new RuntimeException(basename($path) . ' 内容不是合法 JSON');
        }
    }

    // 兼容最初版本（<?php return array(...)），读到后顺手转成新格式。
    if (strpos($raw, '<?php') === 0
        && strpos($raw, 'exit;') === false
        && strpos($raw, 'return') !== false) {
        $legacy = @include $path;
        if (is_array($legacy)) {
            cx_write_store($path, $legacy);
            return $legacy;
        }
    }

    if ($strict) {
        throw new RuntimeException(basename($path) . ' 格式无法识别');
    }
    return null;
}

function cx_write_store(string $path, array $data): bool
{
    $json = json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
    if ($json === false) {
        return false;
    }
    return cx_write_atomic($path, "<?php exit; ?>\n" . $json);
}

function cx_default_config(): array
{
    return [
        'admin_password' => '',
        'api_token'      => '',
        'hmac_secret'    => '',
        'upload_policy'  => 'review', // review=冲突进待审队列；auto=自动接受（历史仍保留）
        'created_at'     => date('c'),
    ];
}

function cx_load_config(): array
{
    $config = cx_read_store(CX_CONFIG_FILE, true);
    return is_array($config) ? array_merge(cx_default_config(), $config) : cx_default_config();
}

function cx_save_config(array $config): bool
{
    return cx_write_store(CX_CONFIG_FILE, $config);
}

/**
 * 状态结构（v2，多设备）：
 *
 *   devices: {
 *     "初一2班": {
 *        note, created_at, updated_at, version, etag,
 *        files:    {'bell.json' => '<原始 JSON 文本>', ...},
 *        commands: [{command, args, apply, dryRun}, ...],
 *        last_seen, last_ip, last_client, last_version
 *     }, ...
 *   }
 *   requests: 最近访问
 *   selected: 管理页当前选中的设备
 *
 * v1（单快照：顶层直接有 files/commands/version）读到后自动包成一台叫「默认设备」的。
 */
function cx_default_device(): array
{
    return [
        'note'         => '',
        'created_at'   => '',
        'updated_at'   => '',
        'version'      => '',
        'etag'         => '',
        'files'        => [],
        'core'         => [], // 新设备可能还没有 core；cx_flat_snapshot/cx_build_manifest 会兜底
        'commands'     => [],
        'last_seen'    => '',
        'last_ip'      => '',
        'last_client'  => '',
        'last_version' => '',
    ];
}

function cx_default_state(): array
{
    return [
        'devices'  => [],
        'requests' => [],
        'selected' => '',
    ];
}

/** 设备名：允许中英文数字、空格与 -_ . ，最长 40，去掉控制字符。 */
function cx_device_name(string $raw): string
{
    $name = trim(preg_replace('/[\x00-\x1F\x7F]/u', '', $raw) ?? '');
    return mb_substr($name, 0, 40);
}

function cx_device_name_ok(string $name): bool
{
    return $name !== '' && preg_match('/^[\p{Han}\p{L}\p{N}][\p{Han}\p{L}\p{N} \-_.()（）]*$/u', $name) === 1;
}

/** 把可能残留的 v1 状态升级成 v2。 */
function cx_upgrade_state(array $state): array
{
    if (isset($state['devices']) && is_array($state['devices'])) {
        foreach ($state['devices'] as $name => $dev) {
            $state['devices'][$name] = array_merge(cx_default_device(), is_array($dev) ? $dev : []);
        }
        return array_merge(cx_default_state(), $state);
    }

    if (isset($state['files']) || isset($state['version']) || isset($state['commands'])) {
        $dev = array_merge(cx_default_device(), [
            'files'      => is_array($state['files'] ?? null) ? $state['files'] : [],
            'commands'   => is_array($state['commands'] ?? null) ? $state['commands'] : [],
            'version'    => (string)($state['version'] ?? ''),
            'etag'       => (string)($state['etag'] ?? ''),
            'updated_at' => (string)($state['updated_at'] ?? ''),
            'created_at' => (string)($state['updated_at'] ?? ''),
        ]);
        return [
            'devices'  => ['默认设备' => $dev],
            'requests' => is_array($state['requests'] ?? null) ? $state['requests'] : [],
            'selected' => '默认设备',
        ];
    }

    return array_merge(cx_default_state(), $state);
}

function cx_load_state(): array
{
    $state = cx_read_store(CX_STATE_FILE, true);
    return cx_upgrade_state(is_array($state) ? $state : cx_default_state());
}

function cx_save_state(array $state): bool
{
    return cx_write_store(CX_STATE_FILE, $state);
}

/** 取某台设备的快照（引用）；不存在返回 null。 */
function &cx_device_ref(array &$state, string $name)
{
    $null = null;
    if ($name === '' || !isset($state['devices'][$name])) {
        return $null;
    }
    return $state['devices'][$name];
}

/** 设备名列表，按创建/更新排序。 */
function cx_device_names(array $state): array
{
    $names = array_keys($state['devices']);
    usort($names, static function (string $a, string $b) use ($state): int {
        $ua = (string)($state['devices'][$a]['updated_at'] ?? '');
        $ub = (string)($state['devices'][$b]['updated_at'] ?? '');
        return strcmp($ub, $ua) ?: strcmp($a, $b);
    });
    return $names;
}

/** 读取请求头（不同 SAPI 拿 Authorization 的方式不一样）。 */
function cx_header(string $name): string
{
    $key = 'HTTP_' . strtoupper(str_replace('-', '_', $name));
    if (!empty($_SERVER[$key])) {
        return (string)$_SERVER[$key];
    }
    if (function_exists('apache_request_headers')) {
        $headers = apache_request_headers();
        foreach ($headers as $k => $v) {
            if (strcasecmp($k, $name) === 0) {
                return (string)$v;
            }
        }
    }
    return '';
}

function cx_bearer_token(): string
{
    $auth = cx_header('Authorization');
    if ($auth !== '' && stripos($auth, 'Bearer ') === 0) {
        return trim(substr($auth, 7));
    }
    // 少数 nginx 配置会把 Authorization 丢掉，留一条退路（管理器手动测试用）。
    return isset($_GET['token']) ? (string)$_GET['token'] : '';
}

function cx_json_out(int $status, array $data, array $headers = []): void
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    foreach ($headers as $h) {
        header($h);
    }
    echo json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

/** 记录一次访问（只保留最近 60 条，存在 state.php 里，不产生明文日志文件）。 */
function cx_log_request(string $action, int $status, string $note = ''): void
{
    try {
        $state = cx_load_state();
        $entry = [
            'at'     => date('Y-m-d H:i:s'),
            'ip'     => $_SERVER['REMOTE_ADDR'] ?? '-',
            'action' => $action,
            'status' => $status,
            'note'   => mb_substr($note, 0, 200),
        ];
        array_unshift($state['requests'], $entry);
        $state['requests'] = array_slice($state['requests'], 0, 60);
        cx_save_state($state);
    } catch (Throwable $ex) {
        // 记日志本身不该让请求失败（存储坏了的时候上层会给明确的 500）。
        error_log('[ClassExland] log failed: ' . $ex->getMessage());
    }
}

function cx_new_token(int $bytes = 24): string
{
    return rtrim(strtr(base64_encode(random_bytes($bytes)), '+/', '-_'), '=');
}

/** 指令队列的对外形态：带 id，客户端按 id 去重，避免重复执行。 */
function cx_command_list(array $device, string $deviceName = ''): array
{
    $commands = [];
    foreach (($device['commands'] ?? []) as $cmd) {
        if (!is_array($cmd) || empty($cmd['command'])) {
            continue;
        }
        $args = new stdClass();
        if (!empty($cmd['args']) && is_array($cmd['args'])) {
            foreach ($cmd['args'] as $k => $v) {
                $args->{(string)$k} = (string)$v;
            }
        }
        $commands[] = [
            'id'      => (string)($cmd['id'] ?? hash('sha256',
                $deviceName . json_encode($cmd, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES))),
            'command' => (string)$cmd['command'],
            'args'    => $args,
            'apply'   => (bool)($cmd['apply'] ?? true),
            'dryRun'  => (bool)($cmd['dryRun'] ?? false),
        ];
    }
    return $commands;
}

/**
 * ============ 核心配置（core）与摊平视图 ============
 *
 * state 里 core 的结构与 manifest 完全一致，原文透传：
 *   {settingsJson, settingsBaseHash, profiles:{name:{json,baseHash}}, layouts:{name:{json,baseHash}}}
 * tree/head/diff/commit 用「摊平视图」：插件文件名原样，core 项加命名空间前缀
 *   core.settingsJson / core.profiles.<name> / core.layouts.<name>
 */
function cx_core_name_ok(string $name): bool
{
    if ($name === '' || strlen($name) > 120) return false;
    if (strpos($name, '/') !== false || strpos($name, '\\') !== false || strpos($name, '..') !== false) return false;
    if (preg_match('/[\x00-\x1F\x7F]/', $name)) return false;
    if (strtolower(substr($name, -5)) !== '.json') return false;
    return !cx_is_forbidden_name($name);
}

/** 文件名命中 PROTOCOL-CORE-SYNC 2.3 的禁止项即拒绝（大小写不敏感）。 */
function cx_is_forbidden_name(string $name): bool
{
    $n = strtolower($name);
    foreach (CX_FORBIDDEN_NAME_FRAGMENTS as $frag) {
        if (strpos($n, $frag) !== false) return true;
    }
    return false;
}

/** 统一白名单判断：插件文件名 + core 命名空间键。 */
function cx_is_allowed_key(string $key): bool
{
    if (in_array($key, CX_ALLOWED_PLUGINS, true)) return true;
    if ($key === CX_CORE_SETTINGS_TREE_KEY) return true;
    foreach ([CX_CORE_PROFILES_PREFIX, CX_CORE_LAYOUTS_PREFIX] as $prefix) {
        if (strpos($key, $prefix) === 0) {
            return cx_core_name_ok(substr($key, strlen($prefix)));
        }
    }
    return false;
}

/** 递归扫描 JSON 键名；任一命中禁止片段即返回 true。 */
function cx_is_forbidden_key(mixed $node, int $depth = 0): bool
{
    if (!is_array($node) || $depth > 32) return false;
    foreach ($node as $key => $value) {
        $k = strtolower((string)$key);
        foreach (CX_FORBIDDEN_KEY_FRAGMENTS as $frag) {
            if (strpos($k, $frag) !== false) return true;
        }
        if (is_array($value) && cx_is_forbidden_key($value, $depth + 1)) return true;
    }
    return false;
}

/** 对原始 JSON 文本做禁止键检测（只用于校验，绝不回写解码结果）。 */
function cx_json_has_forbidden_key(string $raw): bool
{
    if (trim($raw) === '') return false;
    $decoded = json_decode($raw, true);
    if (json_last_error() !== JSON_ERROR_NONE) return false; // 合法性由 cx_is_json 负责
    return cx_is_forbidden_key($decoded);
}

function cx_empty_core(): array
{
    return [
        CX_CORE_SETTINGS   => '',
        'settingsBaseHash' => '',
        CX_CORE_PROFILES   => [],
        CX_CORE_LAYOUTS    => [],
    ];
}

/** 设备快照（files + core）→ 摊平视图 name => 原文。 */
function cx_flat_snapshot(array $device): array
{
    $flat = [];
    foreach (($device['files'] ?? []) as $name => $raw) {
        $name = (string)$name;
        if (cx_is_allowed_key($name) && is_string($raw) && trim($raw) !== '') {
            $flat[$name] = $raw;
        }
    }
    $core = $device['core'] ?? [];
    if (!is_array($core)) return $flat;

    if (isset($core[CX_CORE_SETTINGS]) && is_string($core[CX_CORE_SETTINGS]) && trim($core[CX_CORE_SETTINGS]) !== '') {
        $flat[CX_CORE_SETTINGS_TREE_KEY] = $core[CX_CORE_SETTINGS];
    }
    foreach ([CX_CORE_PROFILES => CX_CORE_PROFILES_PREFIX, CX_CORE_LAYOUTS => CX_CORE_LAYOUTS_PREFIX] as $kind => $prefix) {
        foreach (($core[$kind] ?? []) as $name => $entry) {
            $raw = is_array($entry) ? ($entry['json'] ?? '') : $entry;
            if (is_string($raw) && trim($raw) !== '') {
                $flat[$prefix . (string)$name] = $raw;
            }
        }
    }
    return $flat;
}

/** 设备快照 → baseHash 摊平视图 name => baseHash。 */
function cx_core_base_hashes(array $device): array
{
    $out = [];
    $core = $device['core'] ?? [];
    if (!is_array($core)) return $out;
    if (!empty($core['settingsBaseHash']) && is_string($core['settingsBaseHash'])) {
        $out[CX_CORE_SETTINGS_TREE_KEY] = $core['settingsBaseHash'];
    }
    foreach ([CX_CORE_PROFILES => CX_CORE_PROFILES_PREFIX, CX_CORE_LAYOUTS => CX_CORE_LAYOUTS_PREFIX] as $kind => $prefix) {
        foreach (($core[$kind] ?? []) as $name => $entry) {
            $bh = is_array($entry) ? ($entry['baseHash'] ?? '') : '';
            if (is_string($bh) && $bh !== '') {
                $out[$prefix . (string)$name] = $bh;
            }
        }
    }
    return $out;
}

/** 摊平视图写回设备结构（原文不 decode/encode；baseHash 原样保留）。 */
function cx_apply_snapshot(array &$device, array $flat, array $baseHashes = []): void
{
    $files = [];
    $core = cx_empty_core();
    foreach ($flat as $name => $raw) {
        $name = (string)$name;
        if (!cx_is_allowed_key($name) || !is_string($raw) || trim($raw) === '') continue;
        if ($name === CX_CORE_SETTINGS_TREE_KEY) {
            $core[CX_CORE_SETTINGS] = $raw;
            $core['settingsBaseHash'] = (string)($baseHashes[$name] ?? '');
        } elseif (strpos($name, CX_CORE_PROFILES_PREFIX) === 0 || strpos($name, CX_CORE_LAYOUTS_PREFIX) === 0) {
            $isProfile = strpos($name, CX_CORE_PROFILES_PREFIX) === 0;
            $prefix = $isProfile ? CX_CORE_PROFILES_PREFIX : CX_CORE_LAYOUTS_PREFIX;
            $kind = $isProfile ? CX_CORE_PROFILES : CX_CORE_LAYOUTS;
            $core[$kind][substr($name, strlen($prefix))] = [
                'json' => $raw,
                'baseHash' => (string)($baseHashes[$name] ?? ''),
            ];
        } else {
            $files[$name] = $raw;
        }
    }
    $device['files'] = $files;
    $device['core'] = $core;
}

/**
 * 解析上传请求的 core 字段 → 结构化 core。
 * 校验不通过的内容整条丢弃并写入 $errors；通过的内容原文原样放进 $out。
 */
function cx_parse_core(mixed $core, array &$errors): array
{
    $out = cx_empty_core();
    if ($core === null) return $out;
    if (!is_array($core)) {
        $errors[] = 'core 格式不合法';
        return $out;
    }

    $raw = $core[CX_CORE_SETTINGS] ?? '';
    if ($raw !== null && $raw !== '') {
        $baseHash = (string)($core['settingsBaseHash'] ?? '');
        if (!is_string($raw)) {
            $errors[] = 'settingsJson 必须是原始 JSON 文本';
        } elseif (strlen($raw) > CX_MAX_BODY) {
            $errors[] = 'settingsJson 超过 2 MB';
        } elseif (!cx_is_json($raw)) {
            $errors[] = 'settingsJson 不是合法 JSON';
        } elseif (cx_json_has_forbidden_key($raw)) {
            $errors[] = 'settingsJson 命中禁止键';
        } else {
            $out[CX_CORE_SETTINGS] = $raw;
            $out['settingsBaseHash'] = $baseHash;
        }
    }

    foreach ([CX_CORE_PROFILES => CX_CORE_PROFILES_PREFIX, CX_CORE_LAYOUTS => CX_CORE_LAYOUTS_PREFIX] as $kind => $prefix) {
        $list = $core[$kind] ?? [];
        if (!is_array($list)) {
            $errors[] = $kind . ' 必须是对象';
            continue;
        }
        foreach ($list as $name => $entry) {
            $name = (string)$name;
            if (!cx_core_name_ok($name)) {
                $errors[] = $prefix . $name . ' 文件名不合法或命中禁止项';
                continue;
            }
            $entryRaw = is_array($entry) ? ($entry['json'] ?? '') : $entry;
            $entryHash = is_array($entry) ? (string)($entry['baseHash'] ?? '') : '';
            if (!is_string($entryRaw) || trim($entryRaw) === '') continue;
            if (strlen($entryRaw) > CX_MAX_BODY) {
                $errors[] = $prefix . $name . ' 超过 2 MB';
                continue;
            }
            if (!cx_is_json($entryRaw)) {
                $errors[] = $prefix . $name . ' 不是合法 JSON';
                continue;
            }
            if (cx_json_has_forbidden_key($entryRaw)) {
                $errors[] = $prefix . $name . ' 命中禁止键';
                continue;
            }
            $out[$kind][$name] = ['json' => $entryRaw, 'baseHash' => $entryHash];
        }
    }
    return $out;
}

/**
 * 生成某台设备的清单原文。插件配置与 core 都原样嵌入，不做 decode/encode。
 * $head 参与 etag 计算（由调用方在 cx_publish_files 里重算 etag 时传入）。
 */
function cx_build_manifest(array $device, string $deviceName = '', string $head = ''): string
{
    $pairs = [];
    foreach (($device['files'] ?? []) as $name => $raw) {
        $name = (string)$name;
        if (!cx_is_allowed_key($name) || !is_string($raw) || trim($raw) === '') {
            continue;
        }
        $pairs[] = json_encode($name, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES) . ':' . $raw;
    }

    // core 原文嵌入：每个条目的 json 直接拼接，不经过 json_decode/json_encode。
    $coreParts = [];
    $core = $device['core'] ?? [];
    if (is_array($core)) {
        $settings = $core[CX_CORE_SETTINGS] ?? '';
        if (is_string($settings) && trim($settings) !== '') {
            $coreParts[] = '"settingsJson":' . $settings;
            $coreParts[] = '"settingsBaseHash":'
                . json_encode((string)($core['settingsBaseHash'] ?? ''));
        }
        foreach ([CX_CORE_PROFILES, CX_CORE_LAYOUTS] as $kind) {
            $entries = [];
            $list = $core[$kind] ?? [];
            if (is_array($list)) {
                foreach ($list as $name => $entry) {
                    $raw = is_array($entry) ? ($entry['json'] ?? '') : $entry;
                    $hash = is_array($entry) ? ($entry['baseHash'] ?? '') : '';
                    if (!is_string($raw) || trim($raw) === '') continue;
                    $entries[] = json_encode((string)$name, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)
                        . ':{"json":' . $raw . ',"baseHash":' . json_encode((string)$hash) . '}';
                }
            }
            if ($entries) {
                $coreParts[] = json_encode($kind) . ':{' . implode(',', $entries) . '}';
            }
        }
    }
    $coreJson = $coreParts ? '{' . implode(',', $coreParts) . '}' : 'null';

    $version = ($device['version'] ?? '') !== '' ? (string)$device['version'] : '0';
    $etag    = ($device['etag'] ?? '') !== '' ? (string)$device['etag'] : '0';

    $commands = cx_command_list($device, $deviceName);

    $uploadPath = rtrim(dirname($_SERVER['PHP_SELF'] ?? '/index.php'), '/\\') . '/upload.php';

    return '{'
        . ($deviceName !== '' ? '"device":' . json_encode($deviceName, JSON_UNESCAPED_UNICODE) . ',' : '')
        . ($head !== '' ? '"head":' . json_encode($head) . ',' : '')
        . '"uploadPath":' . json_encode($uploadPath, JSON_UNESCAPED_UNICODE) . ','
        . '"version":' . json_encode($version) . ','
        . '"etag":' . json_encode($etag) . ','
        . '"snapshot":{"pluginConfigs":{' . implode(',', $pairs) . '},"core":' . $coreJson . '},'
        . '"commands":' . json_encode($commands, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)
        . '}';
}

function cx_is_json(string $text): bool
{
    json_decode($text);
    return json_last_error() === JSON_ERROR_NONE;
}

function cx_h(string $text): string
{
    return htmlspecialchars($text, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function cx_pretty(string $raw): string
{
    $decoded = json_decode($raw, true);
    if (json_last_error() !== JSON_ERROR_NONE) {
        return $raw;
    }
    $out = json_encode($decoded, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    return $out === false ? $raw : $out;
}

function cx_current_url(): string
{
    $https = (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off')
        || (($_SERVER['HTTP_X_FORWARDED_PROTO'] ?? '') === 'https');
    return ($https ? 'https' : 'http') . '://' . ($_SERVER['HTTP_HOST'] ?? 'localhost');
}
