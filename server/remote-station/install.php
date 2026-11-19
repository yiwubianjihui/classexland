<?php
declare(strict_types=1);

/**
 * 一次性初始化脚本。只能通过命令行运行，运行后自删。
 *
 *   php install.php '<管理员密码>'
 *
 * 生成 data/config.php（管理员密码哈希、访问令牌、签名密钥）与空的 data/state.php，
 * 把令牌与密钥打印到标准输出，然后把本文件删除。
 */

require __DIR__ . '/lib.php';
cx_boot();

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}

if (file_exists(CX_CONFIG_FILE)) {
    fwrite(STDERR, "already-installed\n");
    exit(2);
}

$password = $argv[1] ?? '';
if (strlen($password) < 8) {
    fwrite(STDERR, "password-too-short\n");
    exit(3);
}

$config = cx_default_config();
$config['admin_password'] = password_hash($password, PASSWORD_DEFAULT);
$config['api_token'] = cx_new_token();
$config['hmac_secret'] = cx_new_token(32);
cx_save_config($config);
cx_save_state(cx_default_state());

echo json_encode([
    'token' => $config['api_token'],
    'hmac'  => $config['hmac_secret'],
]), "\n";

@unlink(__FILE__);
