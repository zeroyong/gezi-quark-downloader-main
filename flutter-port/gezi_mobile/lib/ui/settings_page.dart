import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/download/thread_allocator.dart';
import '../state/app_controller.dart';

/// 设置页。
class SettingsPage extends StatelessWidget {
  const SettingsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<AppController>();
    final settings = controller.settings;
    final scheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('设置')),
      body: ListView(
        padding: const EdgeInsets.symmetric(vertical: 8),
        children: [
          _section('下载', scheme),
          _SliderTile(
            title: '单文件并发连接数',
            subtitle: '默认 ${DownloadTuning.defaultConnections}。'
                '调高会增加功耗与发热，速度不一定提升',
            value: settings.connections,
            min: 1,
            max: DownloadTuning.maxConnections.toDouble(),
            divisions: DownloadTuning.maxConnections - 1,
            onChanged: (v) => controller.updateConnections(v.round()),
          ),
          _SliderTile(
            title: '同时下载任务数',
            subtitle: '建议 1~2。并发任务会互相抢带宽',
            value: settings.concurrentTasks,
            min: 1,
            max: DownloadTuning.maxConcurrentTasks.toDouble(),
            divisions: DownloadTuning.maxConcurrentTasks - 1,
            onChanged: (v) => controller.updateConcurrentTasks(v.round()),
          ),

          const Divider(height: 32),
          _section('外观', scheme),
          RadioListTile<String>(
            title: const Text('跟随系统', style: TextStyle(fontSize: 14)),
            value: 'system',
            // ignore: deprecated_member_use
            groupValue: settings.themeMode,
            // ignore: deprecated_member_use
            onChanged: (v) => controller.updateThemeMode(v ?? 'system'),
            dense: true,
          ),
          RadioListTile<String>(
            title: const Text('亮色', style: TextStyle(fontSize: 14)),
            value: 'light',
            // ignore: deprecated_member_use
            groupValue: settings.themeMode,
            // ignore: deprecated_member_use
            onChanged: (v) => controller.updateThemeMode(v ?? 'light'),
            dense: true,
          ),
          RadioListTile<String>(
            title: const Text('深色', style: TextStyle(fontSize: 14)),
            value: 'dark',
            // ignore: deprecated_member_use
            groupValue: settings.themeMode,
            // ignore: deprecated_member_use
            onChanged: (v) => controller.updateThemeMode(v ?? 'dark'),
            dense: true,
          ),

          const Divider(height: 32),
          _section('账号', scheme),
          ListTile(
            title: const Text('登录状态', style: TextStyle(fontSize: 14)),
            subtitle: Text(
              controller.isLoggedIn ? '已登录' : '未登录（游客模式）',
              style: const TextStyle(fontSize: 12),
            ),
            trailing: Icon(
              controller.isLoggedIn ? Icons.check_circle : Icons.circle_outlined,
              color: controller.isLoggedIn ? Colors.green : scheme.onSurfaceVariant,
              size: 20,
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
            child: Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: scheme.surfaceContainerHighest.withOpacity(0.5),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.info_outline, size: 16, color: scheme.onSurfaceVariant),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      '分享链接游客即可解析和下载，无需登录。\n'
                      '需要下载自己网盘里的文件时才需登录（二期功能）。',
                      style: TextStyle(
                        fontSize: 12,
                        height: 1.5,
                        color: scheme.onSurfaceVariant,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),

          const Divider(height: 32),
          _section('关于', scheme),
          const ListTile(
            title: Text('鸽子下载', style: TextStyle(fontSize: 14)),
            subtitle: Text(
              '夸克网盘下载器 · Flutter 移植版',
              style: TextStyle(fontSize: 12),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
            child: Text(
              '本项目基于 GPL-3.0 发布。\n'
              '仅供学习与技术研究，请勿用于商业用途或侵犯他人版权。\n'
              '软件通过调用夸克网盘的非公开接口实现功能，可能随服务端变更而失效。',
              style: TextStyle(
                fontSize: 11,
                height: 1.6,
                color: scheme.onSurfaceVariant,
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _section(String title, ColorScheme scheme) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
      child: Text(
        title,
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: scheme.primary,
          letterSpacing: 0.5,
        ),
      ),
    );
  }
}

class _SliderTile extends StatelessWidget {
  final String title;
  final String subtitle;
  final int value;
  final double min;
  final double max;
  final int divisions;
  final ValueChanged<double> onChanged;

  const _SliderTile({
    required this.title,
    required this.subtitle,
    required this.value,
    required this.min,
    required this.max,
    required this.divisions,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(title, style: const TextStyle(fontSize: 14)),
              ),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
                decoration: BoxDecoration(
                  color: scheme.primaryContainer,
                  borderRadius: BorderRadius.circular(6),
                ),
                child: Text(
                  '$value',
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w600,
                    color: scheme.onPrimaryContainer,
                  ),
                ),
              ),
            ],
          ),
          Slider(
            value: value.toDouble().clamp(min, max),
            min: min,
            max: max,
            divisions: divisions,
            onChanged: onChanged,
          ),
          Text(
            subtitle,
            style: TextStyle(fontSize: 11, color: scheme.onSurfaceVariant),
          ),
          const SizedBox(height: 8),
        ],
      ),
    );
  }
}
