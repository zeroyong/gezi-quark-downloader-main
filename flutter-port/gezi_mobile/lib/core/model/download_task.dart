import '../download/segmented_downloader.dart';
import '../net/cookie_jar.dart';
import 'task_state.dart';

/// 一个下载任务。
///
/// 对应桌面版 `DownloadJob.cs` + UI 层的 `TaskItem`。
class DownloadTask {
  /// 稳定 ID（用于持久化与查找）。
  final String id;

  /// 展示用文件名。
  final String name;

  /// 文件总字节数。
  final int totalBytes;

  /// 目标文件的**相对路径**（相对于应用文档目录）。
  ///
  /// ⚠️ 为什么不存绝对路径：
  /// iOS 应用每次更新后沙盒容器路径都会变（UUID 段变化），
  /// 存绝对路径会导致所有历史任务在更新后全部失效。
  /// 存相对路径，启动时再拼当前容器路径才是正确做法。
  final String relativePath;

  /// 续传上下文。
  final ResumeContext? resumeContext;

  TaskState state;
  int doneBytes;
  double speed;

  /// 失败/取消的原因。
  String? error;

  /// 分片进度（用于断点续传）。
  List<SegmentSnapshot> segments;

  final DateTime createdAt;
  DateTime updatedAt;

  /// 运行时对象（不持久化）。
  CancellationToken? cancelToken;
  PauseToken? pauseToken;

  DownloadTask({
    required this.id,
    required this.name,
    required this.totalBytes,
    required this.relativePath,
    this.resumeContext,
    this.state = TaskState.queued,
    this.doneBytes = 0,
    this.speed = 0,
    this.error,
    List<SegmentSnapshot>? segments,
    DateTime? createdAt,
    DateTime? updatedAt,
  })  : segments = segments ?? <SegmentSnapshot>[],
        createdAt = createdAt ?? DateTime.now(),
        updatedAt = updatedAt ?? DateTime.now();

  double get percent =>
      totalBytes > 0 ? (doneBytes * 100.0 / totalBytes).clamp(0.0, 100.0) : 0.0;

  bool get isActive => state == TaskState.downloading;

  /// 预计剩余时间。速度过低或未知时返回 null。
  ///
  /// 阈值的用意：速度接近 0 时 `remain / speed` 会算出天文数字
  /// （几百年），显示出来纯属噪音。1 B/s 作为"实质上已停滞"的判据，
  /// 而不是 1KB/s —— 后者会把"很慢但在动"的正常情况也判成未知。
  Duration? get eta {
    if (speed <= 1 || totalBytes <= 0) return null;
    final remain = totalBytes - doneBytes;
    if (remain <= 0) return Duration.zero;
    final seconds = (remain / speed).ceil();
    // 超过 99 小时视为无意义（速度太慢或总量太大），不展示
    if (seconds > 99 * 3600) return null;
    return Duration(seconds: seconds);
  }

  /// 分片进度转成 map（供下载器续传用）。
  Map<int, int> get segmentProgress => <int, int>{
        for (final s in segments) s.index: s.written,
      };

  DownloadTask copyWith({
    TaskState? state,
    int? doneBytes,
    double? speed,
    String? error,
    bool clearError = false,
    List<SegmentSnapshot>? segments,
  }) {
    final copy = DownloadTask(
      id: id,
      name: name,
      totalBytes: totalBytes,
      relativePath: relativePath,
      resumeContext: resumeContext,
      state: state ?? this.state,
      doneBytes: doneBytes ?? this.doneBytes,
      speed: speed ?? this.speed,
      error: clearError ? null : (error ?? this.error),
      segments: segments ?? this.segments,
      createdAt: createdAt,
      updatedAt: DateTime.now(),
    );
    copy.cancelToken = cancelToken;
    copy.pauseToken = pauseToken;
    return copy;
  }

  Map<String, dynamic> toJson() => <String, dynamic>{
        'id': id,
        'name': name,
        'totalBytes': totalBytes,
        'relativePath': relativePath,
        if (resumeContext != null) 'resumeContext': resumeContext!.toJson(),
        'state': state.name,
        'doneBytes': doneBytes,
        'error': error,
        'segments': segments.map((s) => s.toJson()).toList(),
        'createdAt': createdAt.toIso8601String(),
        'updatedAt': updatedAt.toIso8601String(),
      };

  factory DownloadTask.fromJson(Map<String, dynamic> json) {
    final segList = <SegmentSnapshot>[];
    final rawSegments = json['segments'];
    if (rawSegments is List) {
      for (final s in rawSegments) {
        if (s is Map) {
          segList.add(SegmentSnapshot.fromJson(s.cast<String, dynamic>()));
        }
      }
    }

    final rawCtx = json['resumeContext'];
    return DownloadTask(
      id: (json['id'] ?? '').toString(),
      name: (json['name'] ?? '').toString(),
      totalBytes: (json['totalBytes'] as num?)?.toInt() ?? 0,
      relativePath: (json['relativePath'] ?? '').toString(),
      resumeContext:
          rawCtx is Map ? ResumeContext.fromJson(rawCtx.cast<String, dynamic>()) : null,
      state: TaskState.fromName((json['state'] ?? '').toString()),
      doneBytes: (json['doneBytes'] as num?)?.toInt() ?? 0,
      error: json['error']?.toString(),
      segments: segList,
      createdAt: DateTime.tryParse((json['createdAt'] ?? '').toString()),
      updatedAt: DateTime.tryParse((json['updatedAt'] ?? '').toString()),
    );
  }

  @override
  String toString() =>
      'DownloadTask($name, ${percent.toStringAsFixed(1)}%, ${state.label})';
}
