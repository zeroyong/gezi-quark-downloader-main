import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../core/api/quark_client.dart';
import '../core/download/thread_allocator.dart';
import '../core/model/download_task.dart';
import '../core/model/quark_file_entry.dart';
import '../core/model/task_state.dart';
import '../core/util/share_url_parser.dart';
import '../data/settings_repository.dart';
import '../data/task_repository.dart';
import 'download_scheduler.dart';

/// 分享浏览状态。
class ShareBrowseState {
  final bool loading;
  final String? error;
  final String title;
  final String pwdId;
  final String stoken;

  /// 当前所在目录的 fid（'0' = 根目录）。
  final String currentFid;

  /// 面包屑路径（从根到当前）。
  final List<({String fid, String name})> breadcrumbs;

  final List<QuarkFileEntry> entries;
  final int total;
  final int page;
  final bool loadingMore;

  const ShareBrowseState({
    this.loading = false,
    this.error,
    this.title = '',
    this.pwdId = '',
    this.stoken = '',
    this.currentFid = '0',
    this.breadcrumbs = const [],
    this.entries = const [],
    this.total = 0,
    this.page = 1,
    this.loadingMore = false,
  });

  bool get hasMore => entries.length < total;

  ShareBrowseState copyWith({
    bool? loading,
    String? error,
    bool clearError = false,
    String? title,
    String? pwdId,
    String? stoken,
    String? currentFid,
    List<({String fid, String name})>? breadcrumbs,
    List<QuarkFileEntry>? entries,
    int? total,
    int? page,
    bool? loadingMore,
  }) {
    return ShareBrowseState(
      loading: loading ?? this.loading,
      error: clearError ? null : (error ?? this.error),
      title: title ?? this.title,
      pwdId: pwdId ?? this.pwdId,
      stoken: stoken ?? this.stoken,
      currentFid: currentFid ?? this.currentFid,
      breadcrumbs: breadcrumbs ?? this.breadcrumbs,
      entries: entries ?? this.entries,
      total: total ?? this.total,
      page: page ?? this.page,
      loadingMore: loadingMore ?? this.loadingMore,
    );
  }
}

/// 应用主控制器。
///
/// 职责：
///   · 持有 API 客户端与 Cookie 会话（**全局唯一**，见下方说明）
///   · 解析分享链接、浏览目录
///   · 创建下载任务、交给调度器
///   · 持久化设置与任务
class AppController extends ChangeNotifier {
  final TaskRepository taskRepository;
  final SettingsRepository settingsRepository;
  final String rootDir;

  late final QuarkClient _client;
  late DownloadScheduler _scheduler;
  StreamSubscription<DownloadTask>? _schedulerSub;

  AppSettings _settings = const AppSettings();
  List<DownloadTask> _tasks = <DownloadTask>[];
  ShareBrowseState _browse = const ShareBrowseState();
  bool _ready = false;

  AppController({
    required this.taskRepository,
    required this.settingsRepository,
    required this.rootDir,
  });

  // ---------------- 只读状态 ----------------

  AppSettings get settings => _settings;
  List<DownloadTask> get tasks => List.unmodifiable(_tasks);
  ShareBrowseState get browse => _browse;
  bool get ready => _ready;
  bool get isLoggedIn => _client.isLoggedIn;

  /// 正在下载的任务。
  List<DownloadTask> get activeTasks =>
      _tasks.where((t) => t.state == TaskState.downloading).toList();

  /// 已完成的任务。
  List<DownloadTask> get completedTasks =>
      _tasks.where((t) => t.state == TaskState.completed).toList();

  // ---------------- 初始化 ----------------

  Future<void> initialize() async {
    _settings = await settingsRepository.load();
    _tasks = await taskRepository.loadAll();

    // ⚠️ 全局唯一的客户端：Cookie 罐必须贯穿「解析 → 取链 → 下载」全过程。
    // 每次新建客户端会让 CDN 判定会话不一致并返回 412。
    _client = QuarkClient(cookie: _settings.cookie);

    _scheduler = DownloadScheduler(
      repository: taskRepository,
      maxConcurrent: _settings.concurrentTasks,
    );
    _schedulerSub = _scheduler.updates.listen((task) {
      _replaceTask(task);
      notifyListeners();
    });

    _ready = true;
    notifyListeners();
  }

  // ---------------- 分享解析 ----------------

  /// 解析并进入分享。
  ///
  /// 返回错误信息（null 表示成功）。
  Future<String?> openShare(String input) async {
    final info = ShareUrlParser.parse(input);

    if (info.isKouling) {
      return '这是夸克口令而不是分享链接。夸克没有公开的「口令转链接」接口，'
          '请复制完整的分享链接（形如 https://pan.quark.cn/s/xxxx）后再试。';
    }
    if (!info.isValid) {
      return '没有识别到分享链接。请粘贴形如 https://pan.quark.cn/s/xxxx 的链接。';
    }

    _browse = _browse.copyWith(
      loading: true,
      clearError: true,
      title: '',
      pwdId: info.pwdId,
      stoken: '',
      currentFid: '0',
      breadcrumbs: const [],
      entries: const [],
      total: 0,
      page: 1,
    );
    notifyListeners();

    try {
      final tokenResult = await _client.getShareToken(
        info.pwdId,
        passcode: info.passcode,
      );

      final startFid = info.startFid;
      final page = await _client.listShareDir(
        pwdId: info.pwdId,
        stoken: tokenResult.stoken,
        pdirFid: startFid,
      );

      _browse = _browse.copyWith(
        loading: false,
        title: tokenResult.title,
        stoken: tokenResult.stoken,
        currentFid: startFid,
        breadcrumbs: startFid == '0'
            ? const []
            : [(fid: startFid, name: '分享目录')],
        entries: page.items,
        total: page.total,
        page: 1,
      );

      // 记住这次用的链接，方便下次快速重试
      await _updateSettings(lastShareUrl: input);
      notifyListeners();
      return null;
    } catch (e) {
      _browse = _browse.copyWith(loading: false, error: _friendlyError(e));
      notifyListeners();
      return _friendlyError(e);
    }
  }

  /// 进入子目录。
  Future<void> enterDirectory(QuarkFileEntry dir) async {
    if (!dir.isDir || _browse.stoken.isEmpty) return;

    _browse = _browse.copyWith(loading: true, clearError: true);
    notifyListeners();

    try {
      final page = await _client.listShareDir(
        pwdId: _browse.pwdId,
        stoken: _browse.stoken,
        pdirFid: dir.fid,
      );

      _browse = _browse.copyWith(
        loading: false,
        currentFid: dir.fid,
        breadcrumbs: [
          ..._browse.breadcrumbs,
          (fid: dir.fid, name: dir.name),
        ],
        entries: page.items,
        total: page.total,
        page: 1,
      );
      notifyListeners();
    } catch (e) {
      _browse = _browse.copyWith(loading: false, error: _friendlyError(e));
      notifyListeners();
    }
  }

  /// 返回上一级目录。
  Future<void> goUp() async {
    if (_browse.breadcrumbs.isEmpty || _browse.stoken.isEmpty) return;

    final newCrumbs = _browse.breadcrumbs.sublist(0, _browse.breadcrumbs.length - 1);
    final targetFid = newCrumbs.isEmpty ? '0' : newCrumbs.last.fid;

    _browse = _browse.copyWith(loading: true, clearError: true);
    notifyListeners();

    try {
      final page = await _client.listShareDir(
        pwdId: _browse.pwdId,
        stoken: _browse.stoken,
        pdirFid: targetFid,
      );

      _browse = _browse.copyWith(
        loading: false,
        currentFid: targetFid,
        breadcrumbs: newCrumbs,
        entries: page.items,
        total: page.total,
        page: 1,
      );
      notifyListeners();
    } catch (e) {
      _browse = _browse.copyWith(loading: false, error: _friendlyError(e));
      notifyListeners();
    }
  }

  /// 加载下一页（滚动到底时调用）。
  Future<void> loadMore() async {
    if (_browse.loadingMore || !_browse.hasMore || _browse.stoken.isEmpty) {
      return;
    }

    _browse = _browse.copyWith(loadingMore: true);
    notifyListeners();

    try {
      final nextPage = _browse.page + 1;
      final page = await _client.listShareDir(
        pwdId: _browse.pwdId,
        stoken: _browse.stoken,
        pdirFid: _browse.currentFid,
        page: nextPage,
      );

      _browse = _browse.copyWith(
        loadingMore: false,
        entries: [..._browse.entries, ...page.items],
        total: page.total,
        page: nextPage,
      );
      notifyListeners();
    } catch (e) {
      _browse = _browse.copyWith(loadingMore: false, error: _friendlyError(e));
      notifyListeners();
    }
  }

  // ---------------- 下载 ----------------

  /// 下载一个文件。
  ///
  /// 返回错误信息（null 表示已成功加入队列）。
  Future<String?> downloadFile(QuarkFileEntry file) async {
    if (file.isDir) return '目录不能直接下载';

    if (_browse.stoken.isEmpty || _browse.pwdId.isEmpty) {
      return '请先解析分享链接';
    }

    try {
      // 1) 取直链（多入口择优）
      final links = await _client.getShareDownload(
        [file.fid],
        [file.shareToken],
        pwdId: _browse.pwdId,
        stoken: _browse.stoken,
      );
      if (links.isEmpty) return '获取直链失败：返回为空';

      final link = links.first;

      // 2) 构造任务
      final downloadDir =
          await settingsRepository.resolveDownloadDir(_settings);
      // 用相对路径存（iOS 沙盒路径会变）
      final relative = p.join(
        _settings.downloadSubDir.trim().isEmpty
            ? 'downloads'
            : _settings.downloadSubDir.trim(),
        _sanitizeFileName(file.name),
      );
      final destPath = p.join(rootDir, relative);

      final task = DownloadTask(
        id: '${DateTime.now().microsecondsSinceEpoch}_${file.fid}',
        name: file.name,
        totalBytes: link.size > 0 ? link.size : file.size,
        relativePath: relative,
        resumeContext: ResumeContext(
          pwdId: _browse.pwdId,
          stoken: _browse.stoken,
          fid: file.fid,
          shareToken: file.shareToken,
          fromShare: true,
        ),
      );

      _tasks.insert(0, task);
      notifyListeners();

      // 3) 交给调度器
      await _scheduler.start(
        task,
        url: link.url,
        destPath: destPath,
        cookie: _client.cookieString,
        connections: _settings.connections,
        linkRefresher: () async {
          try {
            final fresh = await _client.getShareDownload(
              [file.fid],
              [file.shareToken],
              pwdId: _browse.pwdId,
              stoken: _browse.stoken,
            );
            return fresh.isNotEmpty ? fresh.first.url : null;
          } catch (_) {
            return null;
          }
        },
      );

      // 确保下载目录存在（resolveDownloadDir 已创建，这里只是保险）
      await downloadDir.create(recursive: true);

      await _persistTasks();
      notifyListeners();
      return null;
    } catch (e) {
      return _friendlyError(e);
    }
  }

  /// 暂停任务。
  void pauseTask(DownloadTask task) {
    _scheduler.pause(task.id);
    notifyListeners();
  }

  /// 恢复任务。
  void resumeTask(DownloadTask task) {
    if (_scheduler.isRunning(task.id)) {
      _scheduler.resume(task.id);
    }
    notifyListeners();
  }

  /// 取消任务。
  void cancelTask(DownloadTask task) {
    _scheduler.cancel(task.id);
    notifyListeners();
  }

  /// 删除任务记录（可选择一并删除文件）。
  Future<void> removeTask(DownloadTask task, {bool deleteFile = false}) async {
    if (_scheduler.isRunning(task.id)) {
      _scheduler.cancel(task.id);
    }
    _tasks.removeWhere((t) => t.id == task.id);

    if (deleteFile) {
      try {
        // 相对路径 → 当前容器下的绝对路径
        final file = File(p.join(rootDir, task.relativePath));
        if (await file.exists()) {
          await file.delete();
        }
        // 顺手清理变空的父目录（移动端用户看不到"空目录"，留着只是垃圾）
        final parent = file.parent;
        if (await parent.exists()) {
          final remaining = await parent.list().toList();
          if (remaining.isEmpty) {
            await parent.delete();
          }
        }
      } catch (_) {
        // 删文件失败不该阻止移除任务记录
      }
    }

    await _persistTasks();
    notifyListeners();
  }

  /// 清空已完成的记录。
  Future<void> clearCompleted() async {
    _tasks.removeWhere((t) => t.state == TaskState.completed);
    await _persistTasks();
    notifyListeners();
  }

  // ---------------- 设置 ----------------

  Future<void> updateConnections(int value) async {
    final clamped = value.clamp(1, DownloadTuning.maxConnections);
    await _updateSettings(connections: clamped);
  }

  Future<void> updateConcurrentTasks(int value) async {
    final clamped = value.clamp(1, DownloadTuning.maxConcurrentTasks);
    await _updateSettings(concurrentTasks: clamped);
  }

  Future<void> updateThemeMode(String mode) async {
    await _updateSettings(themeMode: mode);
  }

  Future<void> updateCookie(String cookie) async {
    await _updateSettings(cookie: cookie);
    // Cookie 变了必须重建客户端，否则仍用旧会话
    _client.close();
    _client = QuarkClient(cookie: cookie);
    notifyListeners();
  }

  Future<void> _updateSettings({
    int? connections,
    int? concurrentTasks,
    String? themeMode,
    String? cookie,
    String? lastShareUrl,
  }) async {
    _settings = _settings.copyWith(
      connections: connections,
      concurrentTasks: concurrentTasks,
      themeMode: themeMode,
      cookie: cookie,
      lastShareUrl: lastShareUrl,
    );
    await settingsRepository.save(_settings);
    notifyListeners();
  }

  // ---------------- 内部 ----------------

  void _replaceTask(DownloadTask task) {
    final idx = _tasks.indexWhere((t) => t.id == task.id);
    if (idx >= 0) {
      _tasks[idx] = task;
    }
  }

  Future<void> _persistTasks() => taskRepository.saveAll(_tasks);

  /// 把异常翻译成用户看得懂的话。
  ///
  /// 直接把 `QuarkApiException: ...` 抛给用户等于没给信息。
  String _friendlyError(Object e) {
    final msg = e.toString();
    if (msg.contains('412')) {
      return '请求被拦截（412）。可能是分享已失效或需要登录，请稍后重试。';
    }
    if (msg.contains('401')) {
      return '登录态已失效（401），请重新登录或重新解析分享。';
    }
    if (msg.contains('SocketException') || msg.contains('网络请求失败')) {
      return '网络连接失败，请检查网络后重试。';
    }
    if (msg.contains('TimeoutException')) {
      return '请求超时，请检查网络后重试。';
    }
    return msg;
  }

  /// 清理文件名里的非法字符。
  static String _sanitizeFileName(String name) {
    final cleaned = name.replaceAll(RegExp(r'[\\/:*?"<>|]'), '_').trim();
    if (cleaned.isEmpty) return 'unnamed';
    // 防止路径穿越
    if (cleaned == '.' || cleaned == '..') return 'unnamed';
    return cleaned;
  }

  @override
  void dispose() {
    _schedulerSub?.cancel();
    _scheduler.dispose();
    _client.close();
    super.dispose();
  }
}
