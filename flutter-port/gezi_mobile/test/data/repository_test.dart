import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:gezi_mobile/core/download/segmented_downloader.dart';
import 'package:gezi_mobile/core/model/download_task.dart';
import 'package:gezi_mobile/core/model/task_state.dart';
import 'package:gezi_mobile/data/json_store.dart';
import 'package:gezi_mobile/data/settings_repository.dart';
import 'package:gezi_mobile/data/task_repository.dart';
import 'package:path/path.dart' as p;

void main() {
  late Directory tempDir;

  setUp(() async {
    tempDir = await Directory.systemTemp.createTemp('gezi_test_');
  });

  tearDown(() async {
    try {
      if (await tempDir.exists()) await tempDir.delete(recursive: true);
    } catch (_) {}
  });

  group('JsonStore 原子写入', () {
    test('写入后能读回', () async {
      final store = JsonStore(File(p.join(tempDir.path, 'a.json')));
      expect(await store.write({'x': 1, 'name': '测试'}), isTrue);

      final data = await store.read();
      expect(data, isNotNull);
      expect(data!['x'], 1);
      expect(data['name'], '测试');
    });

    test('文件不存在时返回 null 而非抛异常', () async {
      final store = JsonStore(File(p.join(tempDir.path, 'missing.json')));
      expect(await store.read(), isNull);
    });

    test('损坏的 JSON 返回 null（不能让 App 起不来）', () async {
      final file = File(p.join(tempDir.path, 'broken.json'));
      await file.writeAsString('{"x": 1, "y":');
      final store = JsonStore(file);
      expect(await store.read(), isNull);
    });

    test('空文件返回 null', () async {
      final file = File(p.join(tempDir.path, 'empty.json'));
      await file.writeAsString('');
      expect(await JsonStore(file).read(), isNull);
    });

    test('写入不留 .tmp 残留', () async {
      final store = JsonStore(File(p.join(tempDir.path, 'b.json')));
      await store.write({'a': 1});
      final tmp = File(p.join(tempDir.path, 'b.json.tmp'));
      expect(await tmp.exists(), isFalse, reason: '临时文件应已被 rename 掉');
    });

    test('覆盖写入保持内容完整', () async {
      final store = JsonStore(File(p.join(tempDir.path, 'c.json')));
      await store.write({'v': 1});
      await store.write({'v': 2, 'extra': 'x'});
      final data = await store.read();
      expect(data!['v'], 2);
      expect(data['extra'], 'x');
    });

    test('自动创建父目录', () async {
      final store = JsonStore(
        File(p.join(tempDir.path, 'deep', 'nested', 'd.json')),
      );
      expect(await store.write({'ok': true}), isTrue);
      expect(await File(p.join(tempDir.path, 'deep', 'nested', 'd.json')).exists(),
          isTrue);
    });
  });

  group('TaskRepository', () {
    test('空仓库返回空列表', () async {
      final repo = TaskRepository(rootDir: tempDir.path);
      expect(await repo.loadAll(), isEmpty);
    });

    test('保存后能完整读回', () async {
      final repo = TaskRepository(rootDir: tempDir.path);
      final task = DownloadTask(
        id: 't1',
        name: '测试文件.epub',
        totalBytes: 1024 * 1024,
        relativePath: 'downloads/测试文件.epub',
        doneBytes: 512 * 1024,
        state: TaskState.paused,
      );

      expect(await repo.saveAll([task]), isTrue);
      final loaded = await repo.loadAll();

      expect(loaded.length, 1);
      expect(loaded[0].id, 't1');
      expect(loaded[0].name, '测试文件.epub');
      expect(loaded[0].totalBytes, 1024 * 1024);
      expect(loaded[0].doneBytes, 512 * 1024);
      expect(loaded[0].state, TaskState.paused);
    });

    /// 【关键行为】上次运行时的「下载中」在重启后是不成立的，
    /// 必须归为「已暂停」，否则用户会看到一个永远不动的进度条。
    test('重启后 downloading 状态转为 paused', () async {
      final repo = TaskRepository(rootDir: tempDir.path);
      await repo.saveAll([
        DownloadTask(
          id: 't2',
          name: 'x',
          totalBytes: 100,
          relativePath: 'downloads/x',
          state: TaskState.downloading,
        ),
      ]);

      final loaded = await repo.loadAll();
      expect(loaded[0].state, TaskState.paused);
    });

    test('保留分片进度', () async {
      final repo = TaskRepository(rootDir: tempDir.path);
      await repo.saveAll([
        DownloadTask(
          id: 't3',
          name: 'y',
          totalBytes: 1000,
          relativePath: 'downloads/y',
          segments: const [
            SegmentSnapshot(index: 0, start: 0, end: 499, written: 300),
            SegmentSnapshot(index: 1, start: 500, end: 999, written: 100),
          ],
        ),
      ]);

      final loaded = await repo.loadAll();
      expect(loaded[0].segments.length, 2);
      expect(loaded[0].segments[0].written, 300);
      expect(loaded[0].segmentProgress, {0: 300, 1: 100});
    });

    test('保留续传上下文', () async {
      final repo = TaskRepository(rootDir: tempDir.path);
      await repo.saveAll([
        DownloadTask(
          id: 't4',
          name: 'z',
          totalBytes: 100,
          relativePath: 'downloads/z',
          resumeContext: const ResumeContext(
            pwdId: 'abc123',
            stoken: 'stoken_x',
            fid: 'fid_y',
            shareToken: 'tok_z',
          ),
        ),
      ]);

      final loaded = await repo.loadAll();
      final ctx = loaded[0].resumeContext!;
      expect(ctx.pwdId, 'abc123');
      expect(ctx.stoken, 'stoken_x');
      expect(ctx.fid, 'fid_y');
      expect(ctx.canResume, isTrue);
    });

    /// iOS 应用更新后沙盒路径会变 —— 所以必须存相对路径、现拼绝对路径。
    test('absolutePathOf 用当前根目录现拼（不存绝对路径）', () async {
      final repo = TaskRepository(rootDir: tempDir.path);
      final task = DownloadTask(
        id: 't5',
        name: 'a',
        totalBytes: 1,
        relativePath: p.join('downloads', 'a.epub'),
      );
      final abs = repo.absolutePathOf(task);
      expect(abs, p.join(tempDir.path, 'downloads', 'a.epub'));
      expect(p.isAbsolute(task.relativePath), isFalse,
          reason: '相对路径不能是绝对路径');
    });

    test('单条损坏记录不影响其他记录', () async {
      final repo = TaskRepository(rootDir: tempDir.path);
      await repo.saveAll([
        DownloadTask(
          id: 'good',
          name: 'ok',
          totalBytes: 10,
          relativePath: 'downloads/ok',
        ),
      ]);

      // 手动往存档里塞一条没有 id 的脏数据
      final store = JsonStore(File(p.join(tempDir.path, 'tasks.json')));
      final data = await store.read();
      (data!['tasks'] as List).add({'name': 'no id'});
      await store.write(data);

      final loaded = await repo.loadAll();
      expect(loaded.length, 1);
      expect(loaded[0].id, 'good');
    });
  });

  group('AppSettings', () {
    test('默认值符合移动端调优', () {
      const s = AppSettings();
      expect(s.connections, 16, reason: '桌面版 512 在手机上会烫手');
      expect(s.concurrentTasks, 2);
      expect(s.downloadSubDir, 'downloads');
    });

    test('往返序列化保持一致', () {
      const s = AppSettings(
        connections: 24,
        concurrentTasks: 3,
        downloadSubDir: 'my_dl',
        themeMode: 'dark',
        cookie: '__puus=x',
        lastShareUrl: 'https://pan.quark.cn/s/abc',
      );
      final back = AppSettings.fromJson(s.toJson());
      expect(back.connections, 24);
      expect(back.concurrentTasks, 3);
      expect(back.downloadSubDir, 'my_dl');
      expect(back.themeMode, 'dark');
      expect(back.cookie, '__puus=x');
      expect(back.lastShareUrl, 'https://pan.quark.cn/s/abc');
    });

    test('越界连接数被夹到合法区间', () {
      final tooBig = AppSettings.fromJson({'connections': 99999});
      expect(tooBig.connections, 64);

      final tooSmall = AppSettings.fromJson({'connections': -5});
      expect(tooSmall.connections, 1);
    });

    test('越界任务数被夹到合法区间', () {
      expect(AppSettings.fromJson({'concurrentTasks': 99}).concurrentTasks, 4);
      expect(AppSettings.fromJson({'concurrentTasks': 0}).concurrentTasks, 1);
    });

    test('损坏数据回退默认值', () {
      final s = AppSettings.fromJson({});
      expect(s.connections, 16);
      expect(s.concurrentTasks, 2);
    });
  });

  group('SettingsRepository', () {
    test('无存档时返回默认设置', () async {
      final repo = SettingsRepository(rootDir: tempDir.path);
      final s = await repo.load();
      expect(s.connections, 16);
    });

    test('保存后能读回', () async {
      final repo = SettingsRepository(rootDir: tempDir.path);
      await repo.save(const AppSettings(connections: 32, themeMode: 'dark'));
      final s = await repo.load();
      expect(s.connections, 32);
      expect(s.themeMode, 'dark');
    });

    test('resolveDownloadDir 自动创建目录', () async {
      final repo = SettingsRepository(rootDir: tempDir.path);
      final dir = await repo.resolveDownloadDir(
        const AppSettings(downloadSubDir: 'downloads'),
      );
      expect(await dir.exists(), isTrue);
      expect(dir.path, p.join(tempDir.path, 'downloads'));
    });

    test('downloadSubDir 为空时回退默认', () async {
      final repo = SettingsRepository(rootDir: tempDir.path);
      final dir = await repo.resolveDownloadDir(
        const AppSettings(downloadSubDir: '   '),
      );
      expect(dir.path, p.join(tempDir.path, 'downloads'));
    });
  });

  group('DownloadTask', () {
    test('percent 计算并夹到 0~100', () {
      final t = DownloadTask(
        id: 'a',
        name: 'n',
        totalBytes: 1000,
        relativePath: 'x',
        doneBytes: 500,
      );
      expect(t.percent, 50.0);

      final over = DownloadTask(
        id: 'b',
        name: 'n',
        totalBytes: 100,
        relativePath: 'x',
        doneBytes: 999,
      );
      expect(over.percent, 100.0);
    });

    test('totalBytes 为 0 时 percent 不除零', () {
      final t = DownloadTask(
        id: 'a',
        name: 'n',
        totalBytes: 0,
        relativePath: 'x',
      );
      expect(t.percent, 0.0);
    });

    test('eta 在速度接近停滞时返回 null', () {
      final t = DownloadTask(
        id: 'a',
        name: 'n',
        totalBytes: 1000,
        relativePath: 'x',
        speed: 0, // 完全没动
      );
      expect(t.eta, isNull);
    });

    test('eta 在速度极慢但仍在动时仍可计算', () {
      final t = DownloadTask(
        id: 'a',
        name: 'n',
        totalBytes: 1000,
        relativePath: 'x',
        speed: 100, // 100 B/s，还剩 1000 字节 → 10 秒
      );
      expect(t.eta, const Duration(seconds: 10));
    });

    test('eta 在剩余时间过长时返回 null（避免天文数字）', () {
      final t = DownloadTask(
        id: 'a',
        name: 'n',
        totalBytes: 1024 * 1024 * 1024 * 100, // 100GB
        relativePath: 'x',
        speed: 10, // 10 B/s → 需要约 340 年
      );
      expect(t.eta, isNull);
    });

    test('eta 正常计算', () {
      final t = DownloadTask(
        id: 'a',
        name: 'n',
        totalBytes: 10000,
        relativePath: 'x',
        doneBytes: 5000,
        speed: 1000, // 1KB/s，还剩 5000 字节 → 5 秒
      );
      expect(t.eta, const Duration(seconds: 5));
    });

    test('往返序列化', () {
      final t = DownloadTask(
        id: 'id1',
        name: '文件.mobi',
        totalBytes: 2048,
        relativePath: 'downloads/文件.mobi',
        doneBytes: 1024,
        state: TaskState.failed,
        error: '网络错误',
      );
      final back = DownloadTask.fromJson(t.toJson());
      expect(back.id, 'id1');
      expect(back.name, '文件.mobi');
      expect(back.totalBytes, 2048);
      expect(back.doneBytes, 1024);
      expect(back.state, TaskState.failed);
      expect(back.error, '网络错误');
    });
  });

  group('TaskState', () {
    test('结束态判断', () {
      expect(TaskState.completed.isTerminal, isTrue);
      expect(TaskState.cancelled.isTerminal, isTrue);
      expect(TaskState.failed.isTerminal, isTrue);
      expect(TaskState.downloading.isTerminal, isFalse);
      expect(TaskState.paused.isTerminal, isFalse);
    });

    test('可续传判断', () {
      expect(TaskState.paused.canResume, isTrue);
      expect(TaskState.failed.canResume, isTrue);
      expect(TaskState.completed.canResume, isFalse);
    });

    test('fromName 容错', () {
      expect(TaskState.fromName('downloading'), TaskState.downloading);
      expect(TaskState.fromName('不存在'), TaskState.queued);
    });
  });

  group('ResumeContext', () {
    test('分享模式需要 pwdId/stoken/shareToken 齐全', () {
      const complete = ResumeContext(
        pwdId: 'p',
        stoken: 's',
        fid: 'f',
        shareToken: 't',
      );
      expect(complete.canResume, isTrue);

      const missing = ResumeContext(pwdId: 'p', fid: 'f', shareToken: 't');
      expect(missing.canResume, isFalse, reason: '缺 stoken 无法重取直链');
    });

    test('我的网盘模式只需 fid', () {
      const mine = ResumeContext(fid: 'f', fromShare: false);
      expect(mine.canResume, isTrue);
    });

    test('空 fid 一律不可续传', () {
      expect(const ResumeContext(fid: '').canResume, isFalse);
    });
  });
}
