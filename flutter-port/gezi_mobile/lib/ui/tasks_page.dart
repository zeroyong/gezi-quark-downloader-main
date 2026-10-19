import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/model/download_task.dart';
import '../core/model/task_state.dart';
import '../state/app_controller.dart';
import 'formatters.dart';

/// 下载任务列表。
class TasksPage extends StatelessWidget {
  const TasksPage({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<AppController>();
    final tasks = controller.tasks;

    return Scaffold(
      appBar: AppBar(
        title: const Text('任务'),
        actions: [
          if (tasks.any((t) => t.state == TaskState.completed))
            IconButton(
              tooltip: '清空已完成',
              icon: const Icon(Icons.cleaning_services_outlined),
              onPressed: () => controller.clearCompleted(),
            ),
        ],
      ),
      body: tasks.isEmpty
          ? _buildEmpty(context)
          : ListView.separated(
              padding: const EdgeInsets.symmetric(vertical: 8),
              itemCount: tasks.length,
              separatorBuilder: (_, __) => const SizedBox(height: 8),
              itemBuilder: (context, index) => _TaskCard(
                task: tasks[index],
                controller: controller,
              ),
            ),
    );
  }

  Widget _buildEmpty(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              Icons.download_done_outlined,
              size: 64,
              color: scheme.onSurfaceVariant.withOpacity(0.4),
            ),
            const SizedBox(height: 16),
            Text(
              '还没有下载任务',
              style: TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w500,
                color: scheme.onSurface,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              '在「下载」页解析分享后\n点击文件右侧的下载按钮',
              textAlign: TextAlign.center,
              style: TextStyle(
                fontSize: 13,
                height: 1.6,
                color: scheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _TaskCard extends StatelessWidget {
  final DownloadTask task;
  final AppController controller;

  const _TaskCard({required this.task, required this.controller});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final isActive = task.state == TaskState.downloading;
    final isDone = task.state == TaskState.completed;
    final isFailed = task.state == TaskState.failed;

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 12),
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text(
                    task.name,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                _buildStateChip(scheme),
              ],
            ),
            const SizedBox(height: 10),

            // 进度条
            ClipRRect(
              borderRadius: BorderRadius.circular(3),
              child: LinearProgressIndicator(
                value: isDone ? 1.0 : task.percent / 100,
                minHeight: 5,
                backgroundColor: scheme.surfaceContainerHighest,
                valueColor: AlwaysStoppedAnimation<Color>(
                  isFailed
                      ? scheme.error
                      : isDone
                          ? Colors.green
                          : scheme.primary,
                ),
              ),
            ),
            const SizedBox(height: 8),

            // 统计行
            Row(
              children: [
                Text(
                  '${Formatters.bytes(task.doneBytes)} / '
                  '${Formatters.bytes(task.totalBytes)}',
                  style: TextStyle(
                    fontSize: 12,
                    color: scheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(width: 8),
                Text(
                  '${task.percent.toStringAsFixed(1)}%',
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w500,
                    color: scheme.onSurfaceVariant,
                  ),
                ),
                const Spacer(),
                if (isActive) ...[
                  Text(
                    Formatters.speed(task.speed),
                    style: TextStyle(fontSize: 12, color: scheme.primary),
                  ),
                  const SizedBox(width: 8),
                  Text(
                    '剩余 ${Formatters.duration(task.eta)}',
                    style: TextStyle(
                      fontSize: 12,
                      color: scheme.onSurfaceVariant,
                    ),
                  ),
                ],
              ],
            ),

            // 错误信息
            if (isFailed && task.error != null) ...[
              const SizedBox(height: 8),
              Text(
                task.error!,
                style: TextStyle(fontSize: 12, color: scheme.error),
              ),
            ],

            const SizedBox(height: 6),

            // 操作按钮
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: _buildActions(context, scheme),
            ),
          ],
        ),
      ),
    );
  }

  List<Widget> _buildActions(BuildContext context, ColorScheme scheme) {
    final actions = <Widget>[];

    switch (task.state) {
      case TaskState.downloading:
        actions.add(_btn(
          icon: Icons.pause,
          label: '暂停',
          onTap: () => controller.pauseTask(task),
        ));
        actions.add(_btn(
          icon: Icons.close,
          label: '取消',
          onTap: () => controller.cancelTask(task),
        ));
        break;

      case TaskState.paused:
        actions.add(_btn(
          icon: Icons.play_arrow,
          label: '继续',
          onTap: () => controller.resumeTask(task),
        ));
        actions.add(_btn(
          icon: Icons.close,
          label: '取消',
          onTap: () => controller.cancelTask(task),
        ));
        break;

      case TaskState.failed:
      case TaskState.cancelled:
        actions.add(_btn(
          icon: Icons.refresh,
          label: '重试',
          onTap: () => controller.resumeTask(task),
        ));
        break;

      case TaskState.completed:
      case TaskState.queued:
        break;
    }

    actions.add(_btn(
      icon: Icons.delete_outline,
      label: '删除',
      onTap: () => _confirmDelete(context),
    ));

    return actions;
  }

  Widget _btn({
    required IconData icon,
    required String label,
    required VoidCallback onTap,
  }) {
    return Padding(
      padding: const EdgeInsets.only(left: 4),
      child: TextButton.icon(
        onPressed: onTap,
        icon: Icon(icon, size: 16),
        label: Text(label, style: const TextStyle(fontSize: 12)),
        style: TextButton.styleFrom(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          minimumSize: Size.zero,
          tapTargetSize: MaterialTapTargetSize.shrinkWrap,
        ),
      ),
    );
  }

  Future<void> _confirmDelete(BuildContext context) async {
    final isDone = task.state == TaskState.completed;
    final result = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('删除任务'),
        content: Text(
          isDone
              ? '要同时删除已下载的文件吗？'
              : '删除后本地已下载的部分将丢失，无法续传。',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx, false),
            child: const Text('仅移除记录'),
          ),
          TextButton(
            onPressed: () => Navigator.pop(ctx, true),
            child: Text(
              '同时删除文件',
              style: TextStyle(color: Theme.of(ctx).colorScheme.error),
            ),
          ),
        ],
      ),
    );

    if (result != null) {
      await controller.removeTask(task, deleteFile: result);
    }
  }

  Widget _buildStateChip(ColorScheme scheme) {
    final (color, bg) = switch (task.state) {
      TaskState.downloading => (scheme.primary, scheme.primaryContainer),
      TaskState.completed => (Colors.green, Colors.green.withOpacity(0.12)),
      TaskState.failed => (scheme.error, scheme.errorContainer),
      TaskState.paused => (Colors.orange, Colors.orange.withOpacity(0.12)),
      TaskState.cancelled => (
          scheme.onSurfaceVariant,
          scheme.surfaceContainerHighest
        ),
      TaskState.queued => (
          scheme.onSurfaceVariant,
          scheme.surfaceContainerHighest
        ),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(
        task.state.label,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w500,
          color: color,
        ),
      ),
    );
  }
}
