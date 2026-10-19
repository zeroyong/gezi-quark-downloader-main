import 'dart:async';
import 'dart:io';

import 'package:path/path.dart' as p;

import '../core/model/download_task.dart';
import '../core/model/task_state.dart';
import 'json_store.dart';

/// 任务仓储：持久化下载任务列表。
///
/// ============================================================================
/// 【为什么用 JSON 而不是 SQLite】
///
/// SQLite（drift/sqflite）确实更适合大量任务，但它需要 codegen 或原生插件，
/// 在**没有可用 Android 设备/模拟器**的环境里根本无法运行测试。
/// 本项目的任务量级是几十到几百条，JSON 全量读写完全够用（<10ms）。
///
/// 接口已抽象成仓储形态，将来要换 SQLite 只需替换本类实现，
/// 上层控制器无需改动。
/// ============================================================================
class TaskRepository {
  final JsonStore _store;
  final String _rootDir;

  TaskRepository({required String rootDir})
      : _rootDir = rootDir,
        _store = JsonStore(File(p.join(rootDir, 'tasks.json')));

  /// 应用文档目录（用于把相对路径拼成绝对路径）。
  String get rootDir => _rootDir;

  /// 把任务的相对路径转成当前容器下的绝对路径。
  ///
  /// ⚠️ iOS 沙盒路径在应用更新后会变，所以**必须每次现拼**，
  /// 不能把绝对路径存进 JSON。
  String absolutePathOf(DownloadTask task) =>
      p.join(_rootDir, task.relativePath);

  Future<List<DownloadTask>> loadAll() async {
    final data = await _store.read();
    if (data == null) return <DownloadTask>[];

    final rawList = data['tasks'];
    if (rawList is! List) return <DownloadTask>[];

    final tasks = <DownloadTask>[];
    for (final raw in rawList) {
      if (raw is! Map) continue;
      try {
        final task = DownloadTask.fromJson(raw.cast<String, dynamic>());
        if (task.id.isEmpty) continue;
        // 上次运行时的「下载中」状态是不成立的（进程已死）→ 归为暂停，
        // 让用户能显式恢复，而不是显示一个永远不动的"下载中"。
        if (task.state == TaskState.downloading) {
          task.state = TaskState.paused;
        }
        tasks.add(task);
      } catch (_) {
        // 单条记录损坏不应拖垮整个列表
        continue;
      }
    }
    return tasks;
  }

  Future<bool> saveAll(List<DownloadTask> tasks) {
    return _store.write(<String, dynamic>{
      'version': 1,
      'tasks': tasks.map((t) => t.toJson()).toList(),
    });
  }

  /// 清空存档（测试用）。
  Future<void> clear() => _store.delete();
}
