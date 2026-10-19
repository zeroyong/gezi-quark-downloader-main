import 'dart:io';

import 'package:path/path.dart' as p;

import '../core/download/thread_allocator.dart';
import 'json_store.dart';

/// 应用设置。
class AppSettings {
  /// 单文件并发连接数。
  final int connections;

  /// 同时下载任务数。
  final int concurrentTasks;

  /// 下载目录（相对应用文档目录，空表示用默认 `downloads`）。
  final String downloadSubDir;

  /// 主题模式：system / light / dark。
  final String themeMode;

  /// 保存的登录 Cookie（空表示未登录）。
  ///
  /// ⚠️ 这是明文存储。生产环境应改用 `flutter_secure_storage`
  /// （Android Keystore / iOS Keychain）。当前实现为了零原生依赖暂用明文，
  /// 并已在设置页向用户说明。
  final String cookie;

  /// 上次使用的分享链接（方便用户快速重试）。
  final String lastShareUrl;

  const AppSettings({
    this.connections = DownloadTuning.defaultConnections,
    this.concurrentTasks = DownloadTuning.defaultConcurrentTasks,
    this.downloadSubDir = 'downloads',
    this.themeMode = 'system',
    this.cookie = '',
    this.lastShareUrl = '',
  });

  AppSettings copyWith({
    int? connections,
    int? concurrentTasks,
    String? downloadSubDir,
    String? themeMode,
    String? cookie,
    String? lastShareUrl,
  }) {
    return AppSettings(
      connections: connections ?? this.connections,
      concurrentTasks: concurrentTasks ?? this.concurrentTasks,
      downloadSubDir: downloadSubDir ?? this.downloadSubDir,
      themeMode: themeMode ?? this.themeMode,
      cookie: cookie ?? this.cookie,
      lastShareUrl: lastShareUrl ?? this.lastShareUrl,
    );
  }

  Map<String, dynamic> toJson() => <String, dynamic>{
        'connections': connections,
        'concurrentTasks': concurrentTasks,
        'downloadSubDir': downloadSubDir,
        'themeMode': themeMode,
        'cookie': cookie,
        'lastShareUrl': lastShareUrl,
      };

  factory AppSettings.fromJson(Map<String, dynamic> json) {
    var conn = (json['connections'] as num?)?.toInt() ??
        DownloadTuning.defaultConnections;
    // 防御损坏/手改的配置：夹到合法区间
    if (conn < 1) conn = 1;
    if (conn > DownloadTuning.maxConnections) conn = DownloadTuning.maxConnections;

    var tasks = (json['concurrentTasks'] as num?)?.toInt() ??
        DownloadTuning.defaultConcurrentTasks;
    if (tasks < 1) tasks = 1;
    if (tasks > DownloadTuning.maxConcurrentTasks) {
      tasks = DownloadTuning.maxConcurrentTasks;
    }

    return AppSettings(
      connections: conn,
      concurrentTasks: tasks,
      downloadSubDir: (json['downloadSubDir'] ?? 'downloads').toString(),
      themeMode: (json['themeMode'] ?? 'system').toString(),
      cookie: (json['cookie'] ?? '').toString(),
      lastShareUrl: (json['lastShareUrl'] ?? '').toString(),
    );
  }
}

/// 设置仓储。
class SettingsRepository {
  final JsonStore _store;
  final String _rootDir;

  SettingsRepository({required String rootDir})
      : _rootDir = rootDir,
        _store = JsonStore(File(p.join(rootDir, 'settings.json')));

  Future<AppSettings> load() async {
    final data = await _store.read();
    if (data == null) return const AppSettings();
    try {
      return AppSettings.fromJson(data);
    } catch (_) {
      return const AppSettings();
    }
  }

  Future<bool> save(AppSettings settings) => _store.write(settings.toJson());

  /// 下载目录的绝对路径（不存在则创建）。
  Future<Directory> resolveDownloadDir(AppSettings settings) async {
    final sub = settings.downloadSubDir.trim().isEmpty
        ? 'downloads'
        : settings.downloadSubDir.trim();
    final dir = Directory(p.join(_rootDir, sub));
    if (!await dir.exists()) {
      await dir.create(recursive: true);
    }
    return dir;
  }
}
