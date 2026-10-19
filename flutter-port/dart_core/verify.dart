// 端到端验证：用移植后的核心库跑通
//   分享链接解析 → 换 stoken → 列目录 → 取直链 → 分片并发下载 → 校验完整性
//
// 运行：dart run verify.dart

import 'dart:io';
import 'share_url_parser.dart';
import 'quark_client.dart';
import 'segmented_downloader.dart';
import 'quark_session.dart';

const testUrl = 'https://pan.quark.cn/s/a83dbadbc586';

Future<void> main() async {
  print('=' * 66);
  print('GeZi Flutter 移植核心 —— 端到端验证');
  print('=' * 66);

  // ---------- 1. 链接解析 ----------
  print('\n[1] 分享链接解析');
  final info = ShareUrlParser.parse(testUrl);
  print('    pwdId   = ${info.pwdId}');
  print('    passcode= "${info.passcode}"');
  print('    valid   = ${info.isValid}');
  assert(info.pwdId == 'a83dbadbc586', 'pwdId 解析错误');

  // 文案 + 提取码
  final c1 = ShareUrlParser.parse('链接：https://pan.quark.cn/s/abc123def 提取码：x9k2');
  print('    文案解析: pwdId=${c1.pwdId} passcode=${c1.passcode}');
  // 负向后顾：分享 ID 叫 pwd1234 时不能把 1234 当提取码
  final c2 = ShareUrlParser.parse('https://pan.quark.cn/s/pwd1234');
  print('    边界用例 pwd1234 -> passcode="${c2.passcode}" (应为空)');
  assert(c2.passcode.isEmpty, '负向后顾失效！');

  // ---------- 2. 客户端 & 分享 ----------
  print('\n[2] 换 stoken / 列目录');
  final client = QuarkClient();
  final stoken = await client.getStoken(info.pwdId);
  print('    stoken = ${stoken.substring(0, 20)}...');

  final root = await client.shareDetailPage(info.pwdId, stoken, '0');
  print('    根目录 ${root.items.length} 项 (total=${root.total})');
  for (final e in root.items.take(3)) {
    print('      ${e.isDir ? "[目录]" : "[文件]"} ${e.name}');
  }

  // 递归下潜找第一个真实文件
  print('\n[3] 递归定位文件');
  var page = root;
  var depth = 0;
  QuarkFileEntry? target;
  while (depth < 4 && target == null) {
    final dir = page.items.where((e) => e.isDir).firstOrNull;
    final file = page.items.where((e) => !e.isDir && e.size > 0).firstOrNull;
    if (file != null) {
      target = file;
      break;
    }
    if (dir == null) break;
    depth++;
    page = await client.shareDetailPage(info.pwdId, stoken, dir.fid);
    print('    深度 $depth: ${dir.name} -> ${page.items.length} 项');
  }
  if (target == null) {
    print('    ✘ 未找到可下载文件');
    return;
  }
  final t = target; // 提升为非空
  print('    目标文件: ${t.name}  ${t.size} bytes');

  // ---------- 4. 取直链 ----------
  print('\n[4] 取直链');
  final links = await client.getShareDownload(
    [t.fid],
    [t.token],
    pwdId: info.pwdId,
    stoken: stoken,
  );
  final link = links.first;
  print('    URL host = ${Uri.parse(link.url).host}');
  print('    质量分   = ${QuarkClient.score(link.url)}');
  print('    大小     = ${link.size}');

  // ---------- 5. 分片并发下载 ----------
  print('\n[5] 分片并发下载（8 连接）');
  final dest = '${Directory.systemTemp.path}/gezi_verify/${t.name}';
  final sw = Stopwatch()..start();

  final dl = SegmentedDownloader(
    session: client.session,
    connections: 8,
    linkRefresher: () async {
      print('    ⟳ 直链过期，重新取链...');
      final fresh = await client.getShareDownload(
        [t.fid], [t.token],
        pwdId: info.pwdId, stoken: stoken,
      );
      return fresh.first.url;
    },
  );

  var lastPct = -1;
  final result = await dl.download(
    url: link.url,
    destPath: dest,
    total: t.size,
    onProgress: (p) {
      final pct = p.percent.floor();
      if (pct != lastPct && pct % 25 == 0) {
        lastPct = pct;
        print('    ${pct.toString().padLeft(3)}%  ${(p.speed / 1024).toStringAsFixed(1)} KB/s');
      }
    },
  );
  sw.stop();

  // ---------- 6. 校验 ----------
  print('\n[6] 结果校验');
  final f = File(dest);
  final actual = await f.exists() ? await f.length() : 0;
  print('    下载结果 = ${result.ok}  ${result.message}');
  print('    期望大小 = ${t.size}');
  print('    实际大小 = $actual');
  print('    耗时     = ${sw.elapsedMilliseconds} ms');
  if (sw.elapsedMilliseconds > 0) {
    print('    平均吞吐 = ${(actual / 1024 / 1024 / (sw.elapsedMilliseconds / 1000)).toStringAsFixed(2)} MB/s');
  }
  print('    ${result.ok && actual == t.size ? "✔ 通过：字节数完全一致" : "✘ 失败"}');

  // 清理
  client.close();
  await f.delete();
  try {
    await Directory('${Directory.systemTemp.path}/gezi_verify').delete();
  } catch (_) {}

  print('\n${'=' * 66}');
  print(result.ok && actual == t.size ? '全部验证通过' : '验证未通过');
  print('=' * 66);
}

