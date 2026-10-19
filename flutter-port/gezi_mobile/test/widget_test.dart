import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gezi_mobile/data/settings_repository.dart';
import 'package:gezi_mobile/data/task_repository.dart';
import 'package:gezi_mobile/main.dart';
import 'package:gezi_mobile/state/app_controller.dart';
import 'package:path/path.dart' as p;

/// UI 冒烟测试。
///
/// 用临时目录构造 AppController（**不走 path_provider**），
/// 这样测试能纯跑在 `flutter test` 里，不需要任何设备或插件通道。
void main() {
  late Directory tempDir;
  late AppController controller;

  setUp(() async {
    tempDir = await Directory.systemTemp.createTemp('gezi_ui_');
    controller = AppController(
      taskRepository: TaskRepository(rootDir: tempDir.path),
      settingsRepository: SettingsRepository(rootDir: tempDir.path),
      rootDir: tempDir.path,
    );
    await controller.initialize();
  });

  tearDown(() async {
    controller.dispose();
    try {
      if (await tempDir.exists()) await tempDir.delete(recursive: true);
    } catch (_) {}
  });

  testWidgets('启动后显示首页，三个底部页签都在', (tester) async {
    await tester.pumpWidget(GeziApp(controller: controller));
    await tester.pumpAndSettle();

    expect(find.text('鸽子下载'), findsOneWidget);

    // 底部导航
    expect(find.text('下载'), findsWidgets);
    expect(find.text('任务'), findsWidgets);
    expect(find.text('设置'), findsWidgets);

    // 首页输入区（空状态提示）
    expect(find.text('还没有内容'), findsOneWidget);
    expect(find.text('解析分享'), findsOneWidget);
  });

  testWidgets('切到任务页显示空状态', (tester) async {
    await tester.pumpWidget(GeziApp(controller: controller));
    await tester.pumpAndSettle();

    await tester.tap(find.byIcon(Icons.list_alt_outlined));
    await tester.pumpAndSettle();

    expect(find.text('还没有下载任务'), findsOneWidget);
  });

  testWidgets('切到设置页显示并发数与免责声明', (tester) async {
    await tester.pumpWidget(GeziApp(controller: controller));
    await tester.pumpAndSettle();

    await tester.tap(find.byIcon(Icons.settings_outlined));
    await tester.pumpAndSettle();

    expect(find.text('单文件并发连接数'), findsOneWidget);
    expect(find.text('同时下载任务数'), findsOneWidget);
    expect(find.textContaining('GPL-3.0'), findsOneWidget);
  });

  testWidgets('解析空输入会提示，不发起网络请求', (tester) async {
    await tester.pumpWidget(GeziApp(controller: controller));
    await tester.pumpAndSettle();

    await tester.tap(find.text('解析分享'));
    await tester.pump(); // 让 SnackBar 进场
    await tester.pump(const Duration(milliseconds: 300));

    expect(find.text('请先粘贴分享链接'), findsOneWidget);
    // 没解析成功，就不该出现目录
    expect(controller.browse.pwdId, isEmpty);
  });

  testWidgets('主题切换生效（深色）', (tester) async {
    await tester.pumpWidget(GeziApp(controller: controller));
    await tester.pumpAndSettle();

    expect(controller.settings.themeMode, 'system');

    await controller.updateThemeMode('dark');
    await tester.pumpAndSettle();

    final app = tester.widget<MaterialApp>(find.byType(MaterialApp));
    expect(app.themeMode, ThemeMode.dark);
  });

  test('初始化会创建下载目录所需的数据文件（不误写文件）', () async {
    // 初始化本身不应在根目录写任何东西 —— 设置只在用户改动后才落盘
    final entries = await tempDir.list().toList();
    expect(entries, isEmpty, reason: '首次启动不该产生垃圾文件');
    expect(controller.ready, isTrue);
    expect(controller.tasks, isEmpty);
  });

  test('相对路径拼接使用当前根目录', () {
    final repo = TaskRepository(rootDir: tempDir.path);
    expect(
      repo.rootDir,
      tempDir.path,
      reason: '仓储必须持有注入的根目录，而不是自己去问 path_provider',
    );
    expect(
      p.join(repo.rootDir, 'downloads/a.txt'),
      p.join(tempDir.path, 'downloads/a.txt'),
    );
  });
}
