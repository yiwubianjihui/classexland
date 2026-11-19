<?php
declare(strict_types=1);

/**
 * 插件拉取的清单端点。
 *
 *   GET /manifest.php
 *   Authorization: Bearer <访问令牌>
 *
 * 返回：{"version","etag","snapshot":{"pluginConfigs":{...}},"commands":[...]}
 * 配了签名密钥时，额外返回 X-ClassExland-Signature = hex(hmac_sha256(密钥, 响应原文))。
 */

require __DIR__ . '/lib.php';
cx_boot();

$config = cx_load_config();
if (($config['api_token'] ?? '') === '') {
    cx_json_out(503, ['error' => 'station not initialized']);
}

$token = cx_bearer_token();
if ($token === '' || !hash_equals((string)$config['api_token'], $token)) {
    cx_log_request('manifest', 401, '令牌不匹配');
    cx_json_out(401, ['error' => 'unauthorized']);
}

$state = cx_load_state();
$body  = cx_build_manifest($state);
$sig   = '';
if (($config['hmac_secret'] ?? '') !== '') {
    $sig = hash_hmac('sha256', $body, (string)$config['hmac_secret']);
}

cx_log_request('manifest', 200, 'version=' . ($state['version'] ?: '-') . ' files=' . count($state['files']));

http_response_code(200);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
if ($state['etag'] !== '') {
    header('ETag: "' . $state['etag'] . '"');
}
if ($sig !== '') {
    header('X-ClassExland-Signature: ' . $sig);
}
echo $body;
