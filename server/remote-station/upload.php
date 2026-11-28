<?php
declare(strict_types=1);

/**
 * 客户端上传本地配置快照。
 *
 *   POST /upload.php?device=<设备名>
 *   Authorization: Bearer <访问令牌>
 *   X-ClassExland-Signature: hex(hmac_sha256(密钥, 请求体原文))   // 配了密钥时必填
 *   Content-Type: application/json
 *
 *   {"base":"<上次看到的 head 哈希>","device":"...","message":"...",
 *    "files":{"bell.json":"{...}"},"core":{"settingsJson":"{...}","settingsBaseHash":"...",
 *    "profiles":{"YBH.json":{"json":"{...}","baseHash":"..."}},"layouts":{...}}}
 *
 * base 与服务端 head 一致 → 直接生成一个新提交（fast-forward）；
 * 不一致 → 由服务端裁决：默认进「待审冲突」队列，不覆盖当前快照，管理页可查看 diff 后采用或丢弃。
 * 配置里把 upload_policy 改成 auto 则一律自动接受（仍保留完整历史，不会丢旧版本）。
 * core 为可选：老客户端不传，走原有四/六个插件文件的旧逻辑。
 */

require __DIR__ . '/lib.php';
require __DIR__ . '/history.php';
cx_boot();

// state/history 的读-改-写加互斥锁；exit 时由 shutdown 回调释放。
cx_lock_store(CX_STATE_FILE);
cx_lock_store(CX_HISTORY_FILE);

try {
    $config = cx_load_config();
    $state  = cx_load_state();
    $history = cx_load_history();
} catch (Throwable $ex) {
    cx_json_out(500, ['error' => 'station_storage_error', 'detail' => $ex->getMessage()]);
}

if (($config['api_token'] ?? '') === '') {
    cx_json_out(503, ['error' => 'station not initialized']);
}

$token = cx_bearer_token();
if ($token === '' || !hash_equals((string)$config['api_token'], $token)) {
    cx_log_request('upload', 401, '令牌不匹配');
    cx_json_out(401, ['error' => 'unauthorized']);
}

$body = (string)@file_get_contents('php://input');
if ($body === '') {
    cx_json_out(400, ['error' => 'empty_body']);
}
if (strlen($body) > CX_MAX_BODY * 2) {
    cx_json_out(413, ['error' => 'body_too_large']);
}

// 上行也签名：避免有人拿到令牌后冒充设备写配置。
if (($config['hmac_secret'] ?? '') !== '') {
    $sig = cx_header('X-ClassExland-Signature');
    $expected = hash_hmac('sha256', $body, (string)$config['hmac_secret']);
    // C# 的 Convert.ToHexString() 发大写 hex，PHP hash_hmac 给小写；统一大小写再比。
    if ($sig === '' || !hash_equals(strtolower($expected), strtolower(trim($sig)))) {
        cx_log_request('upload', 401, '请求签名不匹配');
        cx_json_out(401, ['error' => 'bad_signature']);
    }
}

$payload = json_decode($body, true);
if (!is_array($payload)) {
    cx_json_out(400, ['error' => 'invalid_json']);
}

$deviceRaw = (string)($_GET['device'] ?? '');
if ($deviceRaw === '') $deviceRaw = cx_header('X-ClassExland-Device');
if ($deviceRaw === '') $deviceRaw = (string)($payload['device'] ?? '');
$deviceName = cx_device_name($deviceRaw);
if (!cx_device_name_ok($deviceName)) {
    cx_json_out(400, ['error' => 'invalid_device_name']);
}
if (!isset($state['devices'][$deviceName])) {
    if (count($state['devices']) >= CX_MAX_DEVICES) {
        cx_json_out(429, ['error' => 'too_many_devices']);
    }
    $dev = cx_default_device();
    $dev['note'] = '上传时自动登记';
    $dev['created_at'] = date('Y-m-d H:i:s');
    $state['devices'][$deviceName] = $dev;
    cx_save_state($state);
    cx_log_request('device_auto', 200, $deviceName . '（上传）');
}

// 校验文件白名单与 JSON 合法性（PascalCase 原文一定不能在这里被重新编码）。
// 插件文件与 core 统一摊平成 name => 原文，后续 tree/commit/diff 共用同一套逻辑。
$flat = [];
$errors = [];
$incoming = $payload['files'] ?? [];
if (!is_array($incoming)) {
    $errors[] = 'files 必须是对象';
} else {
    foreach ($incoming as $name => $raw) {
        $name = (string)$name;
        if (!cx_is_allowed_key($name)) {
            $errors[] = $name . ' 不在白名单';
            continue;
        }
        if (!is_string($raw) || trim($raw) === '') continue;
        if (strlen($raw) > CX_MAX_BODY) {
            $errors[] = $name . ' 超过 2 MB';
            continue;
        }
        if (!cx_is_json($raw)) {
            $errors[] = $name . ' 不是合法 JSON';
            continue;
        }
        if (cx_is_forbidden_name($name)) {
            $errors[] = $name . ' 命中禁止项';
            continue;
        }
        $flat[$name] = $raw;
    }
}

// core 可选：老客户端不传 core 时保留服务端已有的 core，不能被插件上传顺手清掉。
if (array_key_exists('core', $payload) && $payload['core'] !== null) {
    $core = cx_parse_core($payload['core'], $errors);
    $baseHashes = cx_core_base_hashes(['core' => $core]);
} else {
    $core = is_array($state['devices'][$deviceName]['core'] ?? null)
        ? $state['devices'][$deviceName]['core'] : cx_empty_core();
    $baseHashes = cx_core_base_hashes($state['devices'][$deviceName]);
}
foreach (cx_flat_snapshot(['core' => $core]) as $name => $raw) {
    $flat[$name] = $raw;
}
$pluginKeys = array_values(array_filter(
    array_keys($flat),
    static fn($k) => strpos((string)$k, 'core.') !== 0
));
$coreResult = [
    'settingsJson' => $core[CX_CORE_SETTINGS] !== '',
    'profiles'     => array_keys($core[CX_CORE_PROFILES]),
    'layouts'      => array_keys($core[CX_CORE_LAYOUTS]),
];

if ($errors) {
    cx_json_out(400, ['error' => 'invalid_files', 'detail' => implode('；', $errors)]);
}
if (empty($flat)) {
    cx_json_out(400, ['error' => 'no_files', 'detail' => '没有可上传的配置内容']);
}

$currentFlat = cx_flat_snapshot($state['devices'][$deviceName]);
$head = cx_head_id($history, $deviceName, $currentFlat);
$base = (string)($payload['base'] ?? '');
$message = (string)($payload['message'] ?? '客户端上传');
$hasCommits = !empty($history['devices'][$deviceName]['commits']);

$fastForward = ($base !== '' && $base === $head)
    || ($base === '' && !$hasCommits && empty($currentFlat));

if (!$fastForward && (string)($config['upload_policy'] ?? 'review') === 'auto') {
    // 服务端策略：自动接受。旧版本仍留在历史里，可随时回滚。
    $fastForward = true;
}

if (!$fastForward) {
    // 冲突：服务端Decision = 保留当前版本，把上传内容收进待审队列。
    $pendingTree = [];
    foreach ($flat as $name => $raw) {
        $hash = cx_blob_hash($raw);
        $pendingTree[$name] = $hash;
        $history['blobs'][$hash] = $raw;
    }
    ksort($pendingTree, SORT_STRING);
    $pendingId = hash('sha256', $deviceName . $base . json_encode($pendingTree) . microtime(true));
    $commits = $history['devices'][$deviceName]['commits'] ?? [];
    $lastCommit = $commits ? $commits[count($commits) - 1] : [];
    $lastTree = is_array($lastCommit['tree'] ?? null) ? $lastCommit['tree'] : [];
    $headFiles = cx_commit_files($history, ['tree' => $lastTree]);
    $diff = [];
    foreach ($pendingTree as $name => $hash) {
        $d = cx_text_diff((string)($headFiles[$name] ?? ''), (string)($history['blobs'][$hash] ?? ''), $name);
        if ($d !== '') $diff[$name] = $d;
    }
    $history['devices'][$deviceName]['pending'][] = [
        'id'         => $pendingId,
        'base'       => $base,
        'from'       => '客户端',
        'at'         => date('Y-m-d H:i:s'),
        'message'    => mb_substr($message, 0, 120),
        'tree'       => $pendingTree,
        'baseHashes' => $baseHashes,
        'diff'       => $diff,
    ];
    cx_save_history($history);
    cx_log_request('upload-conflict', 409, $deviceName . ' base=' . mb_substr($base, 0, 8) . ' head=' . mb_substr($head, 0, 8));
    cx_json_out(409, [
        'error'   => 'conflict',
        'head'    => $head,
        'pending' => $pendingId,
        'core'    => $coreResult,
        'message' => '本地版本与服务器不一致，上传内容已存入服务器待审队列，当前快照保持不变。',
    ]);
}

// fast-forward：写 blob + 提交 + 更新当前快照
// parent 必须是父「提交 id」，不是 tree hash（history 的 head 存的就是提交 id）。
$parent = $hasCommits ? (string)($history['devices'][$deviceName]['head'] ?? '') : '';
$newHead = cx_tree_hash($flat);
$commit = cx_commit_snapshot($history, $deviceName, $flat, $message, '客户端', $parent, $baseHashes);
if (!cx_save_history($history)) {
    cx_json_out(500, ['error' => 'history_write_failed']);
}
$version = cx_publish_files($state, $deviceName, $flat, $newHead, $baseHashes);
if (!cx_save_state($state)) {
    cx_json_out(500, ['error' => 'state_write_failed']);
}

cx_log_request('upload', 200, $deviceName . ' version=' . $version . ' head=' . substr($newHead, 0, 12));
cx_json_out(200, [
    'ok'      => true,
    'device'  => $deviceName,
    'head'    => $newHead,
    'tree'    => $newHead,
    'version' => $version,
    'files'   => $pluginKeys,
    'core'    => $coreResult,
]);
