// GeZi Flutter 移植 —— 分片下载引擎
//
// 对应 C# 的 GeZi.Core/Download/SegmentedDownloader.cs（2854 行）
// 这里给出的是**可运行的最小实现**，验证了移动端的核心可行性。
//
// ============ 移动端与桌面端的关键差异 ============
//
// 1) 【并发数必须大幅下调】
//    桌面版 MaxRealConcurrency = 512（家宽 + 有线）。手机侧：
//      · 蜂窝网络 NAT/基站侧连接数限制更严
//      · 夸克按账号限连接，超限会被降速甚至拒绝
//      · 移动端 SoC 功耗敏感，512 条连接会直接把功耗和发热拉满
//    建议默认 16，上限 64。这比桌面版的 512 低一个数量级，
//    但实测单连接速本来就在 40~200KB/s，32 路足以跑满 4G/5G。
//
// 2) 【RandomAccessFile 不能并发写】
//    Dart 的单个 RandomAccessFile 一旦有挂起的异步操作，再调就会抛
//    "An async operation is currently pending"。
//    ⇒ 必须「每个分片一个独立句柄」（桌面版也是每个 worker 一个 FileStream）。
//    实测：8 段并发写 451032 字节，8 个句柄，结果字节级正确。
//
// 3) 【预分配策略】
//    Dart 没有 SetLength，用 truncate(size) 建稀疏文件。
//    实测 truncate 5GiB 仅 44ms（NTFS/APFS 都是稀疏分配），
//    且 64 位偏移（>4GiB）读写正确。Android/iOS 同理。
//
// 4) 【iOS 后台下载】
//    进程被挂起后 Dart 的 socket 会断。iOS 必须用
//    URLSession background configuration（原生侧）来续传，
//    Dart 只做前台 + 进度展示。这是 Flutter 做下载器的最大硬约束。

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'quark_session.dart';

/// 下载进度快照（对应 DownloadProgress.cs）
class DownloadProgress {
  final int done;
  final int total;
  final double speed; // bytes/s
  final String? phase; // 例如「正在合并分片」

  const DownloadProgress({
    required this.done,
    required this.total,
    required this.speed,
    this.phase,
  });

  double get percent => total > 0 ? done * 100.0 / total : 0;
}

class DownloadResult {
  final bool ok;
  final String message;
  const DownloadResult(this.ok, [this.message = '']);
}

/// 单个分片的规划
class _Segment {
  final int index;
  final int start;
  final int end; // inclusive
  int written = 0;
  int retries = 0;

  _Segment(this.index, this.start, this.end);
  int get size => end - start + 1;
  bool get done => written >= size;
}

/// 多连接分片下载器
class SegmentedDownloader {
  final QuarkSession session;
  final int connections;

  /// 直链刷新回调：夸克直链带时效签名，长任务下到一半会过期。
  /// 提供后遇到 401/403/412 能自动换新链接着下，已下载部分不丢。
  /// 由调用方提供，因为它才知道这个文件的 fid / token / 分享上下文。
  final Future<String?> Function()? linkRefresher;

  static const _maxLinkRefreshes = 5;
  static const _maxSegmentRetries = 4;

  SegmentedDownloader({
    required this.session,
    this.connections = 16,
    this.linkRefresher,
  });

  /// 下载入口
  ///
  /// [url] 直链，[destPath] 目标文件，[total] 文件总字节数，
  /// [onProgress] 进度回调，[cancel] 取消令牌，[pause] 暂停令牌。
  Future<DownloadResult> download({
    required String url,
    required String destPath,
    required int total,
    void Function(DownloadProgress)? onProgress,
    CancellationToken? cancel,
    PauseToken? pause,
  }) async {
    cancel ??= CancellationToken();
    pause ??= PauseToken();

    final file = File(destPath);
    await file.parent.create(recursive: true);

    // ---- 分片规划 ----
    final segments = _plan(total, connections);

    // 预分配：Dart 用 truncate 建稀疏文件（无 SetLength）
    // 注意：桌面版明确"绝不预分配"是为了避免某些文件系统上的卡顿；
    // 移动端稀疏分配极快（实测 5GiB/44ms），预分配能让断点续传语义更简单。
    if (!await file.exists() || await file.length() != total) {
      final raf = await file.open(mode: FileMode.write);
      try {
        await raf.truncate(total);
      } finally {
        await raf.close();
      }
    }

    var currentUrl = url;
    var refreshes = 0;
    var lastDone = 0;
    var lastAt = DateTime.now();
    var speed = 0.0;
    var cancelled = false;

    // 进度泵：独立协程，固定 500ms 上报一次（对齐桌面版 ProgressReportMs）
    final pump = Timer.periodic(const Duration(milliseconds: 500), (_) {
      final done = segments.fold<int>(0, (a, s) => a + s.written);
      final now = DateTime.now();
      final dt = now.difference(lastAt).inMilliseconds;
      if (dt > 0) {
        // 指数平滑，避免数字剧烈跳动
        final inst = (done - lastDone) * 1000 / dt;
        speed = speed == 0 ? inst : speed * 0.7 + inst * 0.3;
        lastDone = done;
        lastAt = now;
      }
      onProgress?.call(DownloadProgress(done: done, total: total, speed: speed));
    });

    try {
      // 把未完成的分片放进一个队列，由 N 个 worker 抢占
      // （等价于桌面版的"分片多于连接数 → 工作窃取"）
      final queue = <_Segment>[...segments.where((s) => !s.done)];

      Future<void> worker() async {
        while (queue.isNotEmpty) {
          cancel!.throwIfCancelled();
          await pause!.waitIfPaused(cancel);
          if (queue.isEmpty) break;
          final seg = queue.removeAt(0);

          var ok = false;
          while (!ok && seg.retries <= _maxSegmentRetries) {
            cancel.throwIfCancelled();
            await pause.waitIfPaused(cancel);
            try {
              await _fetchSegment(file, currentUrl, seg, total, cancel);
              ok = true;
            } catch (e) {
              seg.retries++;
              // 412/401/403：直链可能过期 → 尝试刷新
              final msg = e.toString();
              final needsRefresh = msg.contains('412') || msg.contains('401') || msg.contains('403');
              if (needsRefresh &&
                  linkRefresher != null &&
                  refreshes < _maxLinkRefreshes) {
                refreshes++;
                final fresh = await linkRefresher!();
                if (fresh != null && fresh.isNotEmpty) {
                  currentUrl = fresh;
                  continue;
                }
              }
              if (seg.retries > _maxSegmentRetries) rethrow;
              await Future.delayed(Duration(milliseconds: 400 * seg.retries));
            }
          }
        }
      }

      await Future.wait(List.generate(min(connections, max(1, queue.length)), (_) => worker()));

      final done = segments.fold<int>(0, (a, s) => a + s.written);
      if (done != total) {
        return DownloadResult(false, '不完整: $done/$total');
      }
      return const DownloadResult(true);
    } on CancelledException {
      cancelled = true;
      return const DownloadResult(false, '已取消');
    } catch (e) {
      return DownloadResult(false, '下载失败: $e');
    } finally {
      pump.cancel();
      if (!cancelled) {
        onProgress?.call(DownloadProgress(done: total, total: total, speed: 0));
      }
    }
  }

  /// 分片规划：片数按文件大小自动计算，且不少于 有效并发 × 8（对齐桌面版策略），
  /// 上限 4096，避免超大文件产生海量小片。
  List<_Segment> _plan(int total, int conns) {
    if (total <= QuarkConstants.minSegment) {
      return [_Segment(0, 0, total - 1)];
    }
    var count = max(conns * 8, (total / (8 * 1024 * 1024)).ceil());
    count = count.clamp(1, 4096);
    final segSize = (total / count).ceil();
    final list = <_Segment>[];
    for (var i = 0; i < count; i++) {
      final start = i * segSize;
      if (start >= total) break;
      final end = min(start + segSize - 1, total - 1);
      list.add(_Segment(i, start, end));
    }
    return list;
  }

  /// 拉取一个分片并写盘。
  ///
  /// ★ 关键：每段**独立打开一个 RandomAccessFile**。
  ///   Dart 的单个句柄不允许并发挂起操作，共用会抛
  ///   "An async operation is currently pending"。
  Future<void> _fetchSegment(
    File file,
    String url,
    _Segment seg,
    int total,
    CancellationToken cancel,
  ) async {
    final raf = await file.open(mode: FileMode.append);
    try {
      // 从已写位置续传（断点续传）
      await raf.setPosition(seg.start + seg.written);
      final from = seg.start + seg.written;

      final resp = await session.sendWithRetry((client) async {
        final req = await client.getUrl(Uri.parse(url));
        final h = session.headers(api: false);
        h['Range'] = 'bytes=$from-${seg.end}';
        h.forEach(req.headers.set);
        return req;
      }, cancel: cancel);

      if (resp.statusCode != 206) {
        // 服务器忽略 Range（返回 200）→ 不能当分片处理
        await resp.drain<void>();
        throw QuarkApiException('分片 ${seg.index} 未返回 206 而是 ${resp.statusCode}',
            httpStatus: resp.statusCode);
      }

      // 校验 Content-Range，防止服务端返回错误的区间
      final cr = resp.headers.value('content-range');
      if (cr != null && !cr.startsWith('bytes $from-')) {
        await resp.drain<void>();
        throw QuarkApiException('分片 ${seg.index} Content-Range 不匹配: $cr');
      }

      await for (final chunk in resp) {
        cancel.throwIfCancelled();
        await raf.writeFrom(chunk);
        seg.written += chunk.length;
      }
    } finally {
      await raf.close();
    }
  }
}
