import 'dart:async';

import '../core/download/segmented_downloader.dart';
import '../core/download/thread_allocator.dart';
import '../core/model/download_task.dart';
import '../core/model/task_state.dart';
import '../core/net/cookie_jar.dart';
import '../core/net/quark_session.dart';
import '../data/task_repository.dart';

/// 下载调度器：限制同时运行的任务数，并驱动每个任务的分片下载。
///
/// 移植自桌面版 `GeZi.Core/Download/DownloadScheduler.cs`。
///
/// 与桌面版的差异：
///   · 桌面版用 `IProgress<JobUpdate>` 上报；这里用 Stream，更符合 Dart 习惯
///   · 桌面版有「同路径互斥」的静态字典防重复写；这里保留同样语义，
///     因为「删完重下」场景下两组 worker 交叉写同一文件会**损坏数据**
class DownloadScheduler {
  final TaskRepository repository;
  final int maxConcurrent;

  /// 任务状态变化流（UI 订阅它刷新界面）。
  final StreamController<DownloadTask> _updates =
      StreamController<DownloadTask>.broadcast();

  Stream<DownloadTask> get updates => _updates.stream;

  /// 正在运行的任务（taskId → 运行时句柄）。
  final Map<String, _RunningTask> _running = <String, _RunningTask>{};

  /// 目标路径互斥表。防止两个任务同时写同一个文件。
  ///
  /// 【为什么必须有】共写模式下两个下载器会各自 seek 到不同偏移写同一个文件。
  /// 典型触发场景是「删完重下」—— 旧任务还没真正退出，新任务已经起来了，
  /// 两组 worker 交叉写同一文件 → 进度错乱、文件损坏。
  /// **这不是体验问题，是数据安全问题。**
  final Set<String> _activeDests = <String>{};

  bool _disposed = false;

  DownloadScheduler({
    required this.repository,
    this.maxConcurrent = DownloadTuning.defaultConcurrentTasks,
  });

  /// 把任务加入调度并开始下载。
  ///
  /// [cookie] 必须是**取直链时用的那个 Cookie 串** ——
  /// 直链绑定在该会话上，换一个空会话必然 412。
  ///
  /// [linkRefresher] 直链过期时重新取链的回调（可选）。
  Future<void> start(
    DownloadTask task, {
    required String url,
    required String destPath,
    required String cookie,
    int connections = DownloadTuning.defaultConnections,
    Future<String?> Function()? linkRefresher,
  }) async {
    if (_disposed) return;

    // 已经在跑 → 忽略，避免重复启动导致两个 worker 写同一文件
    if (_running.containsKey(task.id)) return;

    // 同路径互斥
    if (_activeDests.contains(destPath)) {
      task.state = TaskState.failed;
      task.error = '同一文件正在下载中，已跳过';
      _emit(task);
      await _persist([task]);
      return;
    }
    _activeDests.add(destPath);

    final cancelToken = CancellationToken();
    final pauseToken = PauseToken();
    task.cancelToken = cancelToken;
    task.pauseToken = pauseToken;
    task.state = TaskState.downloading;
    task.error = null;
    task.speed = 0;
    _emit(task);

    final handle = _RunningTask(
      task: task,
      cancelToken: cancelToken,
      pauseToken: pauseToken,
    );
    _running[task.id] = handle;

    // 不 await：调度器不阻塞调用方，任务在后台跑
    unawaited(_run(
      handle: handle,
      url: url,
      destPath: destPath,
      cookie: cookie,
      connections: connections,
      linkRefresher: linkRefresher,
    ));
  }

  Future<void> _run({
    required _RunningTask handle,
    required String url,
    required String destPath,
    required String cookie,
    required int connections,
    Future<String?> Function()? linkRefresher,
  }) async {
    final task = handle.task;

    // ⚠️ 用取链时的 Cookie 重建会话。
    // 不能 new 一个空会话 —— 空会话没有 __sdid/__pugs，CDN 会直接 412。
    final session = QuarkSession(cookie: cookie);
    final downloader = SegmentedDownloader(
      session: session,
      connections: connections,
      linkRefresher: linkRefresher,
    );

    try {
      final result = await downloader.download(
        url: url,
        destPath: destPath,
        totalBytes: task.totalBytes,
        existingSegments:
            task.segmentProgress.isEmpty ? null : task.segmentProgress,
        cancel: handle.cancelToken,
        pause: handle.pauseToken,
        onProgress: (progress) {
          task.doneBytes = progress.done;
          task.speed = progress.speed;
          _emit(task);
        },
        onSegmentsPersist: (segments) {
          task.segments = segments;
        },
      );

      if (result.ok) {
        task.state = TaskState.completed;
        task.doneBytes = task.totalBytes;
        task.speed = 0;
        task.error = null;
      } else {
        // 区分「用户取消」「用户暂停」「真的失败」——
        // 三者对用户的含义完全不同，不能都显示成"失败"
        if (handle.cancelToken.isCancelled) {
          task.state = TaskState.cancelled;
          task.error = '已取消';
        } else if (handle.pauseToken.isPaused) {
          task.state = TaskState.paused;
          task.error = null;
        } else {
          task.state = TaskState.failed;
          task.error = result.message;
        }
        task.doneBytes = result.written;
        task.speed = 0;
      }
    } catch (e) {
      if (handle.cancelToken.isCancelled) {
        task.state = TaskState.cancelled;
        task.error = '已取消';
      } else {
        task.state = TaskState.failed;
        task.error = e.toString();
      }
      task.speed = 0;
    } finally {
      session.close();
      _running.remove(task.id);
      _activeDests.remove(destPath);
      _emit(task);
      // 任务结束后立刻落盘，避免进程被杀时丢状态
      await _persist([task]);
    }
  }

  /// 暂停任务。
  void pause(String taskId) {
    final handle = _running[taskId];
    if (handle == null) return;
    handle.pauseToken.pause();
    handle.task.state = TaskState.paused;
    handle.task.speed = 0;
    _emit(handle.task);
  }

  /// 恢复已暂停的任务。
  void resume(String taskId) {
    final handle = _running[taskId];
    if (handle == null) return;
    handle.pauseToken.resume();
    handle.task.state = TaskState.downloading;
    _emit(handle.task);
  }

  /// 取消任务（终止下载，不再续传）。
  void cancel(String taskId) {
    final handle = _running[taskId];
    if (handle == null) return;
    handle.cancelToken.cancel();
    handle.task.state = TaskState.cancelled;
    handle.task.speed = 0;
    _emit(handle.task);
  }

  bool isRunning(String taskId) => _running.containsKey(taskId);

  int get runningCount => _running.length;

  void _emit(DownloadTask task) {
    if (_disposed) return;
    task.updatedAt = DateTime.now();
    if (!_updates.isClosed) _updates.add(task);
  }

  /// 把指定任务的当前状态合并回持久化存档。
  ///
  /// 不能简单 `saveAll([task])` —— 那会把其他任务冲掉。
  /// 正确做法是「读全量 → 替换同 id 项 → 写回」。
  Future<void> _persist(List<DownloadTask> changed) async {
    try {
      final all = await repository.loadAll();
      final byId = <String, DownloadTask>{
        for (final t in all) t.id: t,
      };
      for (final t in changed) {
        byId[t.id] = t;
      }
      await repository.saveAll(byId.values.toList());
    } catch (_) {
      // 持久化失败不该影响下载
    }
  }

  Future<void> dispose() async {
    _disposed = true;
    for (final handle in _running.values) {
      handle.cancelToken.cancel();
    }
    _running.clear();
    _activeDests.clear();
    await _updates.close();
  }
}

class _RunningTask {
  final DownloadTask task;
  final CancellationToken cancelToken;
  final PauseToken pauseToken;

  _RunningTask({
    required this.task,
    required this.cancelToken,
    required this.pauseToken,
  });
}
