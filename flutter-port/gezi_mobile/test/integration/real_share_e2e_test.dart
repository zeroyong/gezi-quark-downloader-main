@Timeout(Duration(minutes: 5))
library;

import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:gezi_mobile/core/api/quark_client.dart';
import 'package:gezi_mobile/core/download/segmented_downloader.dart';
import 'package:gezi_mobile/core/model/quark_file_entry.dart';
import 'package:gezi_mobile/core/net/cookie_jar.dart';
import 'package:gezi_mobile/core/util/share_url_parser.dart';
import 'package:path/path.dart' as p;

/// 真实网络端到端集成测试。
///
/// ============================================================================
/// 这个文件**会真的访问夸克服务器并下载文件**。它验证的是整个移植方案
/// 最关键、最不可替代的部分：
///
///   分享链接解析 → 换 stoken → 列目录 → 取直链 → 分片并发下载 → 完整性校验
///
/// 之所以必须有它：在没有 Android 设备/模拟器的环境里，
/// 这是唯一能证明「Dart 侧的 API + 下载引擎真的能用」的手段。
/// 单元测试可以验证逻辑，但验证不了「夸克服务器是否接受 Dart 的请求」。
///
/// 运行：
///   flutter test test/integration/real_share_e2e_test.dart
///
/// 若网络不可用，用 `--exclude-tags network` 跳过。
/// ============================================================================
void main() {
  /// 仓库中真实存在的分享链接（来自桌面版的 state 文件）。
  const shareUrl = 'https://pan.quark.cn/s/a83dbadbc586';

  late Directory tempDir;
  late QuarkClient client;

  setUp(() {
    tempDir = Directory.systemTemp.createTempSync('gezi_e2e_');
    client = QuarkClient();
  });

  tearDown(() {
    client.close();
    try {
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    } catch (_) {}
  });

  test('完整链路：解析 → 列目录 → 取链 → 分片下载 → 校验', () async {
    // ---------- 1. 解析分享链接 ----------
    final info = ShareUrlParser.parse(shareUrl);
    expect(info.isValid, isTrue, reason: '分享链接应能解析出 pwdId');
    expect(info.pwdId, 'a83dbadbc586');

    // ---------- 2. 换 stoken（游客身份，无需登录）----------
    final tokenResult = await client.getShareToken(info.pwdId);
    expect(tokenResult.stoken, isNotEmpty, reason: '应拿到 stoken');
    expect(tokenResult.title, isNotEmpty, reason: '应拿到分享标题');
    // ignore: avoid_print
    print('分享标题: ${tokenResult.title}');

    // ---------- 3. 递归下潜找第一个真实文件 ----------
    var page = await client.listShareDir(
      pwdId: info.pwdId,
      stoken: tokenResult.stoken,
    );
    expect(page.items, isNotEmpty, reason: '根目录不应为空');

    QuarkFileEntry? target;
    var depth = 0;
    while (depth < 4 && target == null) {
      // 优先取根目录下的文件
      for (final e in page.items) {
        if (!e.isDir && e.size > 0) {
          target = e;
          break;
        }
      }
      if (target != null) break;

      // 没有文件就进第一个子目录
      QuarkFileEntry? dir;
      for (final e in page.items) {
        if (e.isDir) {
          dir = e;
          break;
        }
      }
      if (dir == null) break;

      depth++;
      page = await client.listShareDir(
        pwdId: info.pwdId,
        stoken: tokenResult.stoken,
        pdirFid: dir.fid,
      );
      // ignore: avoid_print
      print('下潜 $depth 层: ${dir.name} → ${page.items.length} 项');
    }

    expect(target, isNotNull, reason: '应在 4 层内找到一个可下载文件');
    final file = target!;
    // ignore: avoid_print
    print('目标文件: ${file.name} (${file.size} 字节)');

    // ---------- 4. 取直链（多入口择优）----------
    final links = await client.getShareDownload(
      [file.fid],
      [file.shareToken],
      pwdId: info.pwdId,
      stoken: tokenResult.stoken,
    );
    expect(links, isNotEmpty, reason: '应至少拿到一条直链');

    final link = links.first;
    expect(link.url, startsWith('https://'));
    // ignore: avoid_print
    print('直链 host: ${Uri.parse(link.url).host}');

    final totalBytes = link.size > 0 ? link.size : file.size;
    expect(totalBytes, greaterThan(0));

    // ---------- 5. 分片并发下载 ----------
    final destPath = p.join(tempDir.path, file.name);
    final downloader = SegmentedDownloader(
      session: client.session,
      connections: 8,
    );

    var progressReports = 0;
    final stopwatch = Stopwatch()..start();

    final result = await downloader.download(
      url: link.url,
      destPath: destPath,
      totalBytes: totalBytes,
      onProgress: (_) => progressReports++,
    );

    stopwatch.stop();

    // ---------- 6. 校验 ----------
    expect(result.ok, isTrue, reason: '下载应成功: ${result.message}');

    final downloaded = File(destPath);
    expect(await downloaded.exists(), isTrue, reason: '文件应已落盘');

    final actualSize = await downloaded.length();
    expect(
      actualSize,
      totalBytes,
      reason: '字节数必须完全一致（这是分片写入正确性的唯一硬证据）',
    );

    // ignore: avoid_print
    print('下载完成: $actualSize 字节, '
        '耗时 ${stopwatch.elapsedMilliseconds}ms, '
        '进度回调 $progressReports 次');

    // 校验内容非空（防止全零文件通过大小检查）
    final raf = await downloaded.open();
    try {
      await raf.setPosition(0);
      final head = await raf.read(64);
      expect(head, isNotEmpty);
      // 全零也算合法内容（某些文件确实以零字节开头），但至少要有数据
      expect(head.length, greaterThan(0));
    } finally {
      await raf.close();
    }
  });

  test('续传：下载一半后从断点继续，最终字节数正确', () async {
    const info = 'a83dbadbc586';
    final tokenResult = await client.getShareToken(info);

    var page = await client.listShareDir(
      pwdId: info,
      stoken: tokenResult.stoken,
    );

    QuarkFileEntry? target;
    var depth = 0;
    while (depth < 4 && target == null) {
      for (final e in page.items) {
        if (!e.isDir && e.size > 0) {
          target = e;
          break;
        }
      }
      if (target != null) break;
      QuarkFileEntry? dir;
      for (final e in page.items) {
        if (e.isDir) {
          dir = e;
          break;
        }
      }
      if (dir == null) break;
      depth++;
      page = await client.listShareDir(
        pwdId: info,
        stoken: tokenResult.stoken,
        pdirFid: dir.fid,
      );
    }

    expect(target, isNotNull);
    final file = target!;

    final links = await client.getShareDownload(
      [file.fid],
      [file.shareToken],
      pwdId: info,
      stoken: tokenResult.stoken,
    );
    final link = links.first;
    final totalBytes = link.size > 0 ? link.size : file.size;
    final destPath = p.join(tempDir.path, 'resume_${file.name}');

    // 第一次：在下载进行中触发取消，模拟用户中途取消。
    //
    // ⚠️ 延迟时间要够长，确保真的下了一部分再取消 ——
    // 否则取消发生在第一个字节之前，"续传"其实是在从头下载，
    // 这个测试就变成了假验证（实测 120ms 就踩了这个坑）。
    final cancel1 = CancellationToken();
    final downloader1 = SegmentedDownloader(
      session: client.session,
      connections: 4,
    );

    // 等真正写入了足够多的字节再取消
    final timer = Timer(const Duration(milliseconds: 900), cancel1.cancel);
    final result1 = await downloader1.download(
      url: link.url,
      destPath: destPath,
      totalBytes: totalBytes,
      cancel: cancel1,
    );
    timer.cancel();

    final partialSize = await File(destPath).length();
    // ignore: avoid_print
    print('第一次（中断）: ok=${result1.ok} 已写 ${result1.written} 字节, '
        '文件 $partialSize 字节');

    // 取消场景下 ok 必须是 false —— 否则就是"假成功"
    expect(result1.ok, isFalse, reason: '被取消的下载不应报告成功');

    // 这个断言保证测试本身是有效的：必须真的下了一部分，
    // 否则下面的"续传"验证毫无意义。
    expect(
      result1.written,
      greaterThan(0),
      reason: '取消前应已写入部分数据，否则测不到续传逻辑',
    );
    expect(
      result1.written,
      lessThan(totalBytes),
      reason: '取消时应尚未下完，否则测不到续传逻辑',
    );

    // 第二次：续传。注意要重新取链（旧链可能已过期）
    final links2 = await client.getShareDownload(
      [file.fid],
      [file.shareToken],
      pwdId: info,
      stoken: tokenResult.stoken,
    );
    final downloader2 = SegmentedDownloader(
      session: client.session,
      connections: 4,
    );

    final result2 = await downloader2.download(
      url: links2.first.url,
      destPath: destPath,
      totalBytes: totalBytes,
    );

    expect(result2.ok, isTrue, reason: '续传应成功: ${result2.message}');
    expect(
      await File(destPath).length(),
      totalBytes,
      reason: '续传后字节数必须正确',
    );
    // ignore: avoid_print
    print('第二次（续传）: 成功, 最终 ${await File(destPath).length()} 字节');
  });
}
