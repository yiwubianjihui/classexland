<?php
declare(strict_types=1);

/**
 * ClassExland 远程站 —— 轻量「Git 式」版本库。
 *
 * 不引入外部依赖，也不真的调用 git：只借用它的三个概念——
 *   1. blob：配置内容按 SHA-256 存一次，多个版本/多台设备共用；
 *   2. tree：一次快照的文件名 → blob 哈希映射，再算一个整体哈希当版本号；
 *   3. commit：parent 链 + tree + 与父提交的文本 diff，可回滚、可看改动。
 *
 * 存储独立放在 data/history.php，避免每次清单轮询都重写大文件：
 * state.php 只保留当前快照、最近访问之类的热数据。
 */

const CX_HISTORY_FILE = CX_DATA_DIR . '/history.php';
const CX_MAX_COMMITS  = 40;      // 每台设备保留的最近提交数
const CX_DIFF_LIMIT   = 3000;    // 超过这个行数只做整文件替换的摘要，避免 O(n*m) 爆掉

function cx_default_history(): array
{
    return ['blobs' => [], 'devices' => []];
}

/** 读版本库；读不出来直接抛，绝不当成空库（会丢历史）。 */
function cx_load_history(): array
{
    $raw = cx_read_store(CX_HISTORY_FILE, true);
    $h = cx_default_history();
    if (!is_array($raw)) return $h;

    $out = [
        'blobs'   => is_array($raw['blobs'] ?? null) ? $raw['blobs'] : [],
        'devices' => [],
    ];
    foreach (($raw['devices'] ?? []) as $name => $dev) {
        $out['devices'][$name] = [
            'head'    => (string)($dev['head'] ?? ''),
            'commits' => array_values(is_array($dev['commits'] ?? null) ? $dev['commits'] : []),
            'pending' => array_values(is_array($dev['pending'] ?? null) ? $dev['pending'] : []),
        ];
    }
    return $out;
}

function cx_save_history(array $history): bool
{
    return cx_write_store(CX_HISTORY_FILE, $history);
}

/** 文件内容哈希：与客户端 SHA256(UTF-8 原文) 对齐。 */
function cx_blob_hash(string $raw): string
{
    return hash('sha256', $raw);
}

/** 一次快照的整体哈希（Git tree 的简化版）：文件名排序后把 blob 哈希拼起来再哈希。 */
function cx_tree_hash(array $files): string
{
    $pairs = [];
    foreach ($files as $name => $raw) {
        if (!in_array($name, CX_ALLOWED_KEYS, true) || !is_string($raw) || trim($raw) === '') continue;
        $pairs[$name] = cx_blob_hash($raw);
    }
    ksort($pairs, SORT_STRING);
    return hash('sha256', json_encode($pairs, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));
}

/**
 * 逐行 LCS diff，输出带 @@ 行号的 unified 风格文本（只是给人看，不用于打补丁）。
 * 大文件降级成整文件摘要：版本库里仍然有完整 blob，随时能看原文。
 */
function cx_text_diff(string $old, string $new, string $label = ''): string
{
    $a = preg_split('/\R/u', $old) ?: [];
    $b = preg_split('/\R/u', $new) ?: [];

    if (count($a) > CX_DIFF_LIMIT || count($b) > CX_DIFF_LIMIT) {
        return "@@ 大文件，仅显示摘要 @@\n"
            . '- ' . count($a) . " 行 / " . strlen($old) . " 字节\n"
            . '+ ' . count($b) . " 行 / " . strlen($new) . " 字节\n";
    }

    $n = count($a);
    $m = count($b);

    // dp[i][j] = a[i..] 与 b[j..] 的最长公共子序列长度
    $dp = array_fill(0, $n + 1, array_fill(0, $m + 1, 0));
    for ($i = $n - 1; $i >= 0; $i--) {
        for ($j = $m - 1; $j >= 0; $j--) {
            $dp[$i][$j] = $a[$i] === $b[$j]
                ? $dp[$i + 1][$j + 1] + 1
                : max($dp[$i + 1][$j], $dp[$i][$j + 1]);
        }
    }

    $ops = [];
    $i = 0;
    $j = 0;
    while ($i < $n && $j < $m) {
        if ($a[$i] === $b[$j]) {
            $ops[] = [' ', $a[$i]];
            $i++; $j++;
        } elseif ($dp[$i + 1][$j] >= $dp[$i][$j + 1]) {
            $ops[] = ['-', $a[$i]];
            $i++;
        } else {
            $ops[] = ['+', $b[$j]];
            $j++;
        }
    }
    while ($i < $n) { $ops[] = ['-', $a[$i++]]; }
    while ($j < $m) { $ops[] = ['+', $b[$j++]]; }

    if (!$ops) return $label === '' ? "（无改动）\n" : "--- {$label}\n（无改动）\n";

    // 只保留变化点前后 3 行的上下文，分段显示
    $keep = [];
    foreach ($ops as $idx => $op) if ($op[0] !== ' ') $keep[$idx] = true;
    $show = array_fill(0, count($ops), false);
    foreach (array_keys($keep) as $idx) {
        for ($k = max(0, $idx - 3); $k <= min(count($ops) - 1, $idx + 3); $k++) $show[$k] = true;
    }

    $out = $label === '' ? '' : "--- {$label}\n";
    $oldLine = 1;
    $newLine = 1;
    $segment = [];
    $segOldStart = 1;
    $segNewStart = 1;
    $flush = static function () use (&$segment, &$out, &$segOldStart, &$segNewStart): void {
        if (!$segment) return;
        $out .= "@@ -{$segOldStart}," . count(array_filter($segment, static fn($l) => $l[0] !== '+'))
            . ' +' . "{$segNewStart}," . count(array_filter($segment, static fn($l) => $l[0] !== '-')) . " @@\n";
        foreach ($segment as $line) $out .= $line[0] . ' ' . $line[1] . "\n";
        $segment = [];
    };

    foreach ($ops as $idx => $op) {
        if ($show[$idx]) {
            if (!$segment) { $segOldStart = $oldLine; $segNewStart = $newLine; }
            $segment[] = $op;
        } elseif ($segment) {
            $flush();
        }
        if ($op[0] !== '+') $oldLine++;
        if ($op[0] !== '-') $newLine++;
    }
    $flush();
    return $out === '' ? "（无改动）\n" : $out;
}

/**
 * 提交一次完整快照。返回新 commit。
 * $parent 为空表示根提交；调用方负责校验 base 是否等于当前 head。
 */
function cx_commit_snapshot(array &$history, string $device, array $files, string $message, string $author, string $parent): array
{
    if (!isset($history['devices'][$device])) {
        $history['devices'][$device] = ['head' => '', 'commits' => [], 'pending' => []];
    }

    $tree = [];
    foreach ($files as $name => $raw) {
        if (!in_array($name, CX_ALLOWED_KEYS, true) || !is_string($raw) || trim($raw) === '') continue;
        $hash = cx_blob_hash($raw);
        $tree[$name] = $hash;
        $history['blobs'][$hash] = $raw; // 内容寻址：相同内容只存一份
    }
    ksort($tree, SORT_STRING);

    $oldFiles = [];
    if ($parent !== '' && !empty($history['devices'][$device]['commits'])) {
        foreach ($history['devices'][$device]['commits'] as $c) {
            if (($c['id'] ?? '') === $parent) { $oldFiles = $c['files'] ?? []; break; }
        }
    }

    $diff = [];
    foreach ($tree as $name => $hash) {
        $old = (string)($oldFiles[$name] ?? '');
        $new = (string)($history['blobs'][$hash] ?? '');
        $d = cx_text_diff($old, $new, $name);
        if ($d !== '' && $d !== "（无改动）\n") $diff[$name] = $d;
    }
    foreach ($oldFiles as $name => $raw) {
        if (!isset($tree[$name])) {
            $diff[$name] = "--- {$name}\n+++ （已删除）\n";
        }
    }

    $seed = json_encode([
        'parent'  => $parent,
        'tree'    => $tree,
        'message' => mb_substr($message, 0, 120),
        'at'      => microtime(true),
        'nonce'   => bin2hex(random_bytes(4)),
    ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    $id = hash('sha256', (string)$seed);

    $commit = [
        'id'      => $id,
        'parent'  => $parent,
        'tree'    => $tree,
        'diff'    => $diff,
        'message' => mb_substr($message, 0, 120),
        'author'  => mb_substr($author, 0, 40),
        'at'      => date('Y-m-d H:i:s'),
    ];

    $commits = $history['devices'][$device]['commits'];
    $commits[] = $commit;
    if (count($commits) > CX_MAX_COMMITS) {
        $commits = array_slice($commits, -CX_MAX_COMMITS);
    }
    $history['devices'][$device]['commits'] = $commits;
    $history['devices'][$device]['head'] = $id;

    cx_gc_blobs($history);
    return $commit;
}

/** 回收没人引用的 blob。 */
function cx_gc_blobs(array &$history): void
{
    $used = [];
    foreach ($history['devices'] as $dev) {
        foreach ($dev['commits'] as $c) {
            foreach (($c['tree'] ?? []) as $hash) $used[$hash] = true;
        }
        foreach (($dev['pending'] ?? []) as $p) {
            foreach (($p['tree'] ?? []) as $hash) $used[$hash] = true;
        }
    }
    foreach (array_keys($history['blobs']) as $hash) {
        if (!isset($used[$hash])) unset($history['blobs'][$hash]);
    }
}

/**
 * 当前 head = 现有快照的 tree 哈希（内容寻址）。
 * 不用 commit id：回滚后内容可能和旧提交相同，用内容哈希做 base 比较才能正确快速合并。
 */
function cx_head_id(array $history, string $device, array $currentFiles): string
{
    return cx_tree_hash($currentFiles);
}

/** 把某个提交的 tree 还原成「文件名 → 原文」，用于回滚与管理页预览。 */
function cx_commit_files(array $history, array $commit): array
{
    $files = [];
    foreach (($commit['tree'] ?? []) as $name => $hash) {
        if (isset($history['blobs'][$hash])) $files[$name] = $history['blobs'][$hash];
    }
    return $files;
}

/** 更新 state.php 里的当前快照（manifest 直接读它，保证旧协议不变）。 */
function cx_publish_files(array &$state, string $device, array $files): string
{
    $version = date('Ymd-His') . '-' . substr(bin2hex(random_bytes(2)), 0, 4);
    $state['devices'][$device]['files'] = $files;
    $state['devices'][$device]['version'] = $version;
    $state['devices'][$device]['updated_at'] = date('Y-m-d H:i:s');
    $state['devices'][$device]['etag'] = substr(
        hash('sha256', cx_build_manifest($state['devices'][$device], $device)), 0, 12
    );
    $state['selected'] = $device;
    return $version;
}
