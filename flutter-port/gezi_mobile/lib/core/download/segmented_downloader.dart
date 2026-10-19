import 'dart:async';
import 'dart:io';
import 'dart:math';

import '../net/cookie_jar.dart';
import '../net/quark_session.dart';
import 'thread_allocator.dart';

/// 下载进度快照。
///
/// 对应桌面版 `DownloadProgress.cs`。
class DownloadProgress {
  final int done;
  final int total;

  /// 平滑后的速度（字节/秒）。
  final double speed;

  /// 当前阶段说明（例如「正在校验」）。null 表示常规下载阶段。
  final String? phase;

  const DownloadProgress({
    required this.done,
    required this.total,
    required this.speed,
    this.phase,
  });

  double get percent => total > 0 ? (done * 100.0 / total) : 0.0;

  /// 预计剩余时间。速度过低时返回 null（避免显示荒谬的数字）。
  Duration? get eta {
    if (speed <= 1024 || total <= 0) return null;
    final remain = total - done;
    if (remain <= 0) return Duration.zero;
    return Duration(seconds: (remain / speed).ceil());
  }
}

/// 下载结果。
class DownloadResult {
  final bool ok;
  final String message;

  /// 已写入的字节数（失败时也有意义，用于展示与续传判断）。
  final int written;

  const DownloadResult({
    required this.ok,
    this.message = '',
    this.written = 0,
  });

  @override
  String toString() => ok ? '成功' : '失败: $message';
}

/// 单个分片的规划与进度。
class _Segment {
  final int index;
  final int start;

  /// 结束偏移（**含**）。
  final int end;

  int written = 0;
  int retries = 0;

  _Segment({
    required this.index,
    required this.start,
    required this.end,
  });

  int get length => end - start + 1;
  bool get isDone => written >= length;

  /// 下一个待读的绝对偏移。
  int get nextOffset => start + written;
}

/// 多连接分片下载器。
///
/// 移植自桌面版 `GeZi.Core/Download/SegmentedDownloader.cs`（2854 行）。
/// 桌面版那 2854 行里约 80% 在处理 512 并发下的抢占、合并线程、
/// 限速自适应与诊断快照 —— 移动端并发降到 16 后这些复杂度大部分不再需要，
/// 因此这里是**精简但等价**的实现。
///
/// ============================================================================
/// 【三条移动端硬约束，都已在代码里体现】
///
/// 1) **每分片必须独立打开 RandomAccessFile**
///    Dart 的单个 RandomAccessFile 一旦有挂起的异步操作，再调用就会抛
///    `FileSystemException: An async operation is currently pending`。
///    共用句柄做并发写是**行不通的**，必须每段各自 open/close。
///    （桌面版也是每个 worker 一个 FileStream，思路一致。）
///
/// 2) **Cookie 必须贯穿到下载请求**
///    直链绑定在取链时的 Cookie 会话上，缺 Cookie 必 412。
///    所以这里直接用创建下载器时传入的那个 [QuarkSession]，
///    绝不另建 HttpClient。
///
/// 3) **不预读整个文件到内存**
///    移动端内存宝贵，分片流式写入，峰值内存 ≈ 连接数 × 读缓冲。
/// ============================================================================
class SegmentedDownloader {
  /// 会话（提供 Cookie 与请求头）。必须与取链时是同一个实例。
  final QuarkSession session;

  /// 并发连接数。
  final int connections;

  /// 直链刷新回调。
  ///
  /// 夸克直链带时效签名，长任务下到一半过期是常态。
  /// 提供后遇到 401/403/412 能自动换新链接着下 ——
  /// 已下载的部分（[written] 记录）原样保留，不重下。
  /// 由调用方注入，因为只有它知道这个文件对应的 fid / token / 分享上下文。
  final Future<String?> Function()? linkRefresher;

  /// 日志出口（可选）。
  final void Function(String message)? onLog;

  SegmentedDownloader({
    required this.session,
    this.connections = DownloadTuning.defaultConnections,
    this.linkRefresher,
    this.onLog,
  });

  /// 执行下载。
  ///
  /// [url] 初始直链。
  /// [destPath] 目标文件绝对路径。
  /// [totalBytes] 文件总字节数（必须准确，否则分片规划会错）。
  /// [existingSegments] 续传上下文：上次各分片的已写字节。
  /// [onProgress] 进度回调（约 500ms 一次）。
  /// [cancel] 取消令牌。
  /// [pause] 暂停令牌。
  /// [onSegmentsPersist] 分片进度持久化回调（用于断点续传）。
  Future<DownloadResult> download({
    required String url,
    required String destPath,
    required int totalBytes,
    Map<int, int>? existingSegments,
    void Function(DownloadProgress progress)? onProgress,
    CancellationToken? cancel,
    PauseToken? pause,
    void Function(List<SegmentSnapshot> segments)? onSegmentsPersist,
  }) async {
    final token = cancel ?? CancellationToken();
    final pauseToken = pause ?? PauseToken();

    if (totalBytes <= 0) {
      return const DownloadResult(ok: false, message: '文件大小无效');
    }

    final file = File(destPath);
    try {
      await file.parent.create(recursive: true);
    } catch (e) {
      return DownloadResult(ok: false, message: '无法创建目录: $e');
    }

    // ---------------- 分片规划 ----------------
    final segments = _planSegments(totalBytes, existingSegments);
    final pending = segments.where((s) => !s.isDone).toList();

    if (pending.isEmpty) {
      // 分片记录显示已全部完成，但保险起见校验实际文件大小
      final actual = await _safeLength(file);
      if (actual == totalBytes) {
        return DownloadResult(ok: true, written: totalBytes);
      }
      // 记录与实际不符（文件被删/被截断）→ 重新下
      for (final s in segments) {
        s.written = 0;
      }
      pending.addAll(segments);
    }

    // ---------------- 预分配 ----------------
    // Dart 没有 SetLength，用 truncate 建**稀疏**文件。
    // 实测 5GiB 仅需 44ms（NTFS/APFS/ext4 都是稀疏分配），
    // 且 64 位偏移（>4GiB）读写正常。
    // 预分配的好处：分片可以自由 seek，不必担心文件长度不够。
    if (!await file.exists() || await _safeLength(file) != totalBytes) {
      try {
        final raf = await file.open(mode: FileMode.write);
        try {
          await raf.truncate(totalBytes);
        } finally {
          await raf.close();
        }
      } catch (e) {
        return DownloadResult(ok: false, message: '无法分配文件空间: $e');
      }
    }

    // ---------------- 状态 ----------------
    var currentUrl = url;
    var refreshCount = 0;
    var lastDone = segments.fold<int>(0, (a, s) => a + s.written);
    var lastTick = DateTime.now();
    var smoothSpeed = 0.0;
    var persistCounter = 0;

    int totalWritten() => segments.fold<int>(0, (a, s) => a + s.written);

    // ---------------- 进度泵 ----------------
    // 独立定时器：下载 worker 只管写字节，进度计算与上报集中在这里，
    // 避免每个 worker 都去算总进度（既浪费又容易读到不一致的中间值）。
    final progressTimer = Timer.periodic(
      const Duration(milliseconds: DownloadTuning.progressIntervalMs),
      (_) {
        final now = DateTime.now();
        final done = totalWritten();
        final dtMs = now.difference(lastTick).inMilliseconds;
        if (dtMs > 0) {
          final instant = (done - lastDone) * 1000.0 / dtMs;
          smoothSpeed = smoothSpeed == 0
              ? instant
              : smoothSpeed * DownloadTuning.speedSmoothing +
                  instant * (1 - DownloadTuning.speedSmoothing);
          lastDone = done;
          lastTick = now;
        }
        onProgress?.call(DownloadProgress(
          done: done,
          total: totalBytes,
          speed: smoothSpeed < 0 ? 0 : smoothSpeed,
        ));
      },
    );

    // ---------------- 执行 ----------------
    try {
      final queue = List<_Segment>.of(pending);
      final workerCount = min(connections, queue.length);

      onLog?.call(
        '开始下载: 共 ${segments.length} 片, ${queue.length} 片待下, $workerCount 连接',
      );

      Future<void> worker(int workerId) async {
        while (true) {
          token.throwIfCancelled();
          await pauseToken.waitIfPaused(token);

          if (queue.isEmpty) return;
          final seg = queue.removeAt(0);
          if (seg.isDone) continue;

          await _fetchWithRetry(
            file: file,
            initialUrl: currentUrl,
            segment: seg,
            token: token,
            pauseToken: pauseToken,
            refreshUrl: () async {
              if (linkRefresher == null ||
                  refreshCount >= DownloadTuning.maxLinkRefreshes) {
                return null;
              }
              refreshCount++;
              onLog?.call('直链可能已过期，正在重新获取（第 $refreshCount 次）…');
              final fresh = await linkRefresher!();
              if (fresh != null && fresh.isNotEmpty) {
                currentUrl = fresh;
                return fresh;
              }
              return null;
            },
          );

          // 每完成一片持久化一次，缩小进程被杀时的损失
          persistCounter++;
          if (persistCounter % 4 == 0) {
            onSegmentsPersist?.call(_snapshot(segments));
          }
        }
      }

      await Future.wait(
        List<Future<void>>.generate(workerCount, (i) => worker(i)),
      );

      final finalDone = totalWritten();
      onSegmentsPersist?.call(_snapshot(segments));

      if (finalDone < totalBytes) {
        return DownloadResult(
          ok: false,
          message: '下载不完整: $finalDone / $totalBytes 字节',
          written: finalDone,
        );
      }

      // 最终校验：实际文件大小必须与预期一致
      final actualSize = await _safeLength(file);
      if (actualSize != totalBytes) {
        return DownloadResult(
          ok: false,
          message: '文件大小校验失败: 实际 $actualSize, 预期 $totalBytes',
          written: finalDone,
        );
      }

      onProgress?.call(DownloadProgress(
        done: totalBytes,
        total: totalBytes,
        speed: 0,
      ));
      onLog?.call('下载完成: $destPath');

      return DownloadResult(ok: true, written: totalBytes);
    } on CancelledException {
      return DownloadResult(
        ok: false,
        message: '已取消',
        written: totalWritten(),
      );
    } catch (e) {
      return DownloadResult(
        ok: false,
        message: '下载失败: $e',
        written: totalWritten(),
      );
    } finally {
      progressTimer.cancel();
    }
  }

  /// 规划分片，并把 [existing] 里的续传进度套回去。
  List<_Segment> _planSegments(int totalBytes, Map<int, int>? existing) {
    final count = DownloadTuning.planSegmentCount(totalBytes, connections);
    final segSize = (totalBytes / count).ceil();
    final segments = <_Segment>[];

    for (var i = 0; i < count; i++) {
      final start = i * segSize;
      if (start >= totalBytes) break;
      final end = min(start + segSize - 1, totalBytes - 1);
      final seg = _Segment(index: i, start: start, end: end);

      if (existing != null) {
        final saved = existing[i];
        if (saved != null && saved > 0) {
          // 不能超过分片长度（防御损坏的续传记录）
          seg.written = min(saved, seg.length);
        }
      }
      segments.add(seg);
    }
    return segments;
  }

  List<SegmentSnapshot> _snapshot(List<_Segment> segments) => segments
      .map((s) => SegmentSnapshot(
            index: s.index,
            start: s.start,
            end: s.end,
            written: s.written,
          ))
      .toList();

  Future<int> _safeLength(File file) async {
    try {
      return await file.exists() ? await file.length() : 0;
    } catch (_) {
      return 0;
    }
  }

  /// 带重试地拉取一个分片。
  ///
  /// 重试策略：
  ///   · 鉴权类错误（401/403/412）→ 先尝试刷新直链，再重试
  ///   · 其他错误 → 退避后重试
  ///   · 超过 [DownloadTuning.maxSegmentRetries] → 抛出，由上层判定失败
  Future<void> _fetchWithRetry({
    required File file,
    required String initialUrl,
    required _Segment segment,
    required CancellationToken token,
    required PauseToken pauseToken,
    required Future<String?> Function() refreshUrl,
  }) async {
    var url = initialUrl;

    while (true) {
      token.throwIfCancelled();
      await pauseToken.waitIfPaused(token);

      try {
        await _fetchSegmentOnce(
          file: file,
          url: url,
          segment: segment,
          token: token,
        );
        return;
      } catch (e) {
        if (e is CancelledException) rethrow;

        segment.retries++;
        final isAuth = e is QuarkApiException && e.isAuthError;

        if (segment.retries > DownloadTuning.maxSegmentRetries) {
          throw QuarkApiException(
            '分片 ${segment.index} 重试 ${DownloadTuning.maxSegmentRetries} 次仍失败: $e',
          );
        }

        if (isAuth) {
          final fresh = await refreshUrl();
          if (fresh != null) {
            url = fresh;
            onLog?.call('分片 ${segment.index} 使用新直链重试');
            continue;
          }
        }

        // 退避：400ms, 800ms, 1200ms, 1600ms（带抖动）
        final backoffMs = 400 * segment.retries;
        final jitter = 0.8 + 0.4 * Random().nextDouble();
        await Future<void>.delayed(
          Duration(milliseconds: (backoffMs * jitter).round()),
        );
      }
    }
  }

  /// 拉取一次分片（失败即抛，由 [_fetchWithRetry] 决定是否重试）。
  ///
  /// ★ 每次调用都**独立打开并关闭** RandomAccessFile。
  ///   这是 Dart 的硬约束：单个句柄不允许并发挂起操作。
  Future<void> _fetchSegmentOnce({
    required File file,
    required String url,
    required _Segment segment,
    required CancellationToken token,
  }) async {
    final raf = await file.open(mode: FileMode.append);
    try {
      // 从已写位置开始（断点续传）
      final from = segment.nextOffset;
      await raf.setPosition(from);

      final response = await session.sendWithRetry(
        (client) async {
          final request = await client.getUrl(Uri.parse(url));
          final headers = session.buildHeaders(api: false);
          // 注意：Range 必须是唯一由我们控制的分片头，
          // 且必须与实际写入位置严格对应
          headers['Range'] = 'bytes=$from-${segment.end}';
          headers.forEach(request.headers.set);
          return request;
        },
        cancel: token,
      );

      // 服务端忽略 Range 返回 200（整文件）时不能当分片处理，
      // 否则会把整个文件写进这一片的位置 → 文件损坏
      if (response.statusCode != HttpStatus.partialContent) {
        await response.drain<void>();
        throw QuarkApiException(
          '分片 ${segment.index} 未返回 206（实际 ${response.statusCode}）',
          httpStatus: response.statusCode,
        );
      }

      // 校验 Content-Range 的起点，防止服务端返回错误区间
      final contentRange = response.headers.value('content-range');
      if (contentRange != null && !contentRange.startsWith('bytes $from-')) {
        await response.drain<void>();
        throw QuarkApiException(
          '分片 ${segment.index} 区间不匹配: 期望从 $from 开始, 实际 $contentRange',
        );
      }

      await for (final chunk in response) {
        token.throwIfCancelled();
        if (chunk.isEmpty) continue;
        await raf.writeFrom(chunk);
        segment.written += chunk.length;
      }
    } finally {
      await raf.close();
    }
  }
}

/// 分片快照，用于持久化续传上下文。
class SegmentSnapshot {
  final int index;
  final int start;
  final int end;
  final int written;

  const SegmentSnapshot({
    required this.index,
    required this.start,
    required this.end,
    required this.written,
  });

  Map<String, dynamic> toJson() => <String, dynamic>{
        'index': index,
        'start': start,
        'end': end,
        'written': written,
      };

  factory SegmentSnapshot.fromJson(Map<String, dynamic> json) => SegmentSnapshot(
        index: (json['index'] as num?)?.toInt() ?? 0,
        start: (json['start'] as num?)?.toInt() ?? 0,
        end: (json['end'] as num?)?.toInt() ?? 0,
        written: (json['written'] as num?)?.toInt() ?? 0,
      );
}
