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
const CX_ALLOWED_KEYS = ['bell.json', 'duty.json', 'activities.json', 'layout.json'];
const CX_MAX_BODY     = 524288; // 512 KB
const CX_MAX_DEVICES  = 32;     // 一个人身兼多个班级也够用了

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

/**
 * 生成某台设备的清单原文。插件配置原样嵌入，不做 decode/encode。
 */
function cx_build_manifest(array $device, string $deviceName = ''): string
{
    $pairs = [];
    foreach (($device['files'] ?? []) as $name => $raw) {
        if (!in_array($name, CX_ALLOWED_KEYS, true)) {
            continue;
        }
        if (!is_string($raw) || trim($raw) === '') {
            continue;
        }
        $pairs[] = json_encode($name, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES) . ':' . $raw;
    }

    $version = ($device['version'] ?? '') !== '' ? (string)$device['version'] : '0';
    $etag    = ($device['etag'] ?? '') !== '' ? (string)$device['etag'] : '0';

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
            'command' => (string)$cmd['command'],
            'args'    => $args,
            'apply'   => (bool)($cmd['apply'] ?? true),
            'dryRun'  => (bool)($cmd['dryRun'] ?? false),
        ];
    }

    return '{'
        . ($deviceName !== '' ? '"device":' . json_encode($deviceName, JSON_UNESCAPED_UNICODE) . ',' : '')
        . '"version":' . json_encode($version) . ','
        . '"etag":' . json_encode($etag) . ','
        . '"snapshot":{"pluginConfigs":{' . implode(',', $pairs) . '}},'
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
