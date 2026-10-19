import 'dart:async';

import 'package:flutter/material.dart';
import 'package:path_provider/path_provider.dart';
import 'package:provider/provider.dart';

import 'data/settings_repository.dart';
import 'data/task_repository.dart';
import 'state/app_controller.dart';
import 'ui/home_page.dart';
import 'ui/settings_page.dart';
import 'ui/tasks_page.dart';
import 'ui/theme.dart';

/// 应用根目录。
///
/// 所有持久化（设置、任务存档、下载文件）都挂在**应用文档目录**下：
///   · Android → /data/user/0/<pkg>/app_flutter
///   · iOS     → <容器>/Documents
///
/// 为什么不放外部存储：Android 10+ 的分区存储让外部路径写起来很别扭，
/// 而应用私有目录**不需要任何运行时权限**就能读写，卸载时自动清理。
/// 用户要把文件拿出去时，走系统的「打开方式 / 分享」即可（见任务页）。
Future<String> resolveRootDir() async {
  final dir = await getApplicationDocumentsDirectory();
  return dir.path;
}

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  try {
    final rootDir = await resolveRootDir();

    final controller = AppController(
      taskRepository: TaskRepository(rootDir: rootDir),
      settingsRepository: SettingsRepository(rootDir: rootDir),
      rootDir: rootDir,
    );
    await controller.initialize();

    runApp(GeziApp(controller: controller));
  } catch (e) {
    // 初始化失败（目录不可写、存档损坏到无法恢复等）时不能白屏 ——
    // 用户看到一句人话，比看着一个纯白界面无从下手要好。
    runApp(_StartupErrorApp(message: e.toString()));
  }
}

/// 应用根 widget。
///
/// 单独抽出来（而不是塞在 [main] 里）是为了让 widget 测试能注入
/// 一个用临时目录构造的 controller，不必真的去碰 path_provider。
class GeziApp extends StatelessWidget {
  final AppController controller;

  const GeziApp({super.key, required this.controller});

  @override
  Widget build(BuildContext context) {
    return ChangeNotifierProvider<AppController>.value(
      value: controller,
      // 用 .value 而不是 .create：controller 的生命周期等同于进程，
      // 由 main 持有；让 Provider 去 dispose 反而会与它自己的
      // 异步收尾（关闭 HttpClient、取消任务）打架。
      // 用 Selector 而不是 Consumer：AppController 每 500ms 会因下载进度
      // 触发一次 notifyListeners，若这里 watch 整个 controller，
      // MaterialApp 会被跟着重建 2 次/秒 —— 主题其实只在设置页改过才变。
      child: Selector<AppController, String>(
        selector: (_, app) => app.settings.themeMode,
        builder: (context, themeMode, _) {
          return MaterialApp(
            title: '鸽子下载',
            debugShowCheckedModeBanner: false,
            theme: AppTheme.light(),
            darkTheme: AppTheme.dark(),
            themeMode: _themeModeOf(themeMode),
            home: const AppShell(),
          );
        },
      ),
    );
  }

  static ThemeMode _themeModeOf(String value) {
    switch (value) {
      case 'light':
        return ThemeMode.light;
      case 'dark':
        return ThemeMode.dark;
      default:
        return ThemeMode.system;
    }
  }
}

/// 主框架：底部三页。
///
/// 用 [IndexedStack] 而不是每次重建页面，是为了让「下载」页的目录状态、
/// 滚动位置在切到「任务」再切回来时**还在** —— 用户点了个下载、去任务页
/// 看一眼进度、再回来继续挑文件，这是本应用最主流的操作路径，
/// 每次回来都退回根目录会非常恼人。
class AppShell extends StatefulWidget {
  const AppShell({super.key});

  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  int _index = 0;

  @override
  Widget build(BuildContext context) {
    // 同样用 Selector：只需要「有几个任务在跑」这一个整数，
    // 没必要跟着每个进度 tick 重建整个导航栏。
    final activeCount = context.select<AppController, int>(
      (app) => app.activeTasks.length,
    );

    return Scaffold(
      body: IndexedStack(
        index: _index,
        children: const [
          HomePage(),
          TasksPage(),
          SettingsPage(),
        ],
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _index,
        onDestinationSelected: (i) => setState(() => _index = i),
        destinations: [
          const NavigationDestination(
            icon: Icon(Icons.cloud_download_outlined),
            selectedIcon: Icon(Icons.cloud_download),
            label: '下载',
          ),
          NavigationDestination(
            icon: Badge(
              // 只在下任务时显示角标：0 个还在跳的角标纯属视觉噪音
              isLabelVisible: activeCount > 0,
              label: Text('$activeCount'),
              child: const Icon(Icons.list_alt_outlined),
            ),
            selectedIcon: Badge(
              isLabelVisible: activeCount > 0,
              label: Text('$activeCount'),
              child: const Icon(Icons.list_alt),
            ),
            label: '任务',
          ),
          const NavigationDestination(
            icon: Icon(Icons.settings_outlined),
            selectedIcon: Icon(Icons.settings),
            label: '设置',
          ),
        ],
      ),
    );
  }
}

/// 启动失败时的兜底界面。
class _StartupErrorApp extends StatelessWidget {
  final String message;

  const _StartupErrorApp({required this.message});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      home: Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Icon(Icons.error_outline, size: 56, color: Colors.redAccent),
                const SizedBox(height: 16),
                const Text(
                  '启动失败',
                  style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 12),
                const Text(
                  '应用无法初始化本地存储。请确认设备存储空间充足后重试；'
                  '若问题持续，卸载重装可清除损坏的存档。',
                  textAlign: TextAlign.center,
                  style: TextStyle(fontSize: 13, height: 1.6),
                ),
                const SizedBox(height: 16),
                SelectableText(
                  message,
                  textAlign: TextAlign.center,
                  style: const TextStyle(fontSize: 11, color: Colors.grey),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
