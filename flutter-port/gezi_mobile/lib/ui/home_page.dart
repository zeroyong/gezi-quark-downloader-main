import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/model/quark_file_entry.dart';
import '../core/util/share_url_parser.dart';
import '../state/app_controller.dart';
import 'formatters.dart';

/// 首页：粘贴分享链接 → 解析 → 浏览文件。
class HomePage extends StatefulWidget {
  const HomePage({super.key});

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  final TextEditingController _linkController = TextEditingController();
  final ScrollController _scrollController = ScrollController();
  bool _parsing = false;

  @override
  void initState() {
    super.initState();
    _scrollController.addListener(_onScroll);
    // 预填上次使用的链接，方便用户快速重试
    final last = context.read<AppController>().settings.lastShareUrl;
    if (last.isNotEmpty) _linkController.text = last;
  }

  @override
  void dispose() {
    _linkController.dispose();
    _scrollController.dispose();
    super.dispose();
  }

  void _onScroll() {
    if (!_scrollController.hasClients) return;
    final pos = _scrollController.position;
    // 距底部 400px 内就开始加载下一页，避免用户看到"卡住"
    if (pos.pixels >= pos.maxScrollExtent - 400) {
      context.read<AppController>().loadMore();
    }
  }

  Future<void> _pasteAndParse() async {
    final data = await Clipboard.getData(Clipboard.kTextPlain);
    if (data?.text != null && data!.text!.isNotEmpty) {
      _linkController.text = data.text!;
      if (mounted) await _parse();
    } else {
      _toast('剪贴板里没有文本');
    }
  }

  Future<void> _parse() async {
    final input = _linkController.text.trim();
    if (input.isEmpty) {
      _toast('请先粘贴分享链接');
      return;
    }

    // 先做一次本地预检，给出更准确的提示（不用等网络往返）
    final info = ShareUrlParser.parse(input);
    if (info.isKouling) {
      _toast('这是夸克口令，不是分享链接。请复制完整链接（pan.quark.cn/s/...）');
      return;
    }

    setState(() => _parsing = true);
    final error = await context.read<AppController>().openShare(input);
    if (!mounted) return;
    setState(() => _parsing = false);

    if (error != null) _toast(error);
  }

  void _toast(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(message),
          behavior: SnackBarBehavior.floating,
          duration: const Duration(seconds: 3),
        ),
      );
  }

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<AppController>();
    final browse = controller.browse;

    return Scaffold(
      appBar: AppBar(
        title: const Text('鸽子下载'),
        actions: [
          if (browse.breadcrumbs.isNotEmpty)
            IconButton(
              tooltip: '返回上级',
              icon: const Icon(Icons.arrow_upward),
              onPressed: () => controller.goUp(),
            ),
          IconButton(
            tooltip: '刷新',
            icon: const Icon(Icons.refresh),
            onPressed: browse.pwdId.isEmpty ? null : _parse,
          ),
        ],
      ),
      body: Column(
        children: [
          _buildLinkInput(),
          if (browse.error != null) _buildError(browse.error!),
          if (browse.title.isNotEmpty) _buildBreadcrumb(controller),
          Expanded(child: _buildBody(controller)),
        ],
      ),
    );
  }

  Widget _buildLinkInput() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
      child: Column(
        children: [
          TextField(
            controller: _linkController,
            maxLines: 2,
            minLines: 1,
            style: const TextStyle(fontSize: 14),
            decoration: InputDecoration(
              hintText: '粘贴夸克分享链接，或整段分享文案',
              prefixIcon: const Icon(Icons.link, size: 20),
              suffixIcon: IconButton(
                tooltip: '从剪贴板粘贴',
                icon: const Icon(Icons.content_paste, size: 20),
                onPressed: _parsing ? null : _pasteAndParse,
              ),
            ),
            onSubmitted: (_) => _parse(),
          ),
          const SizedBox(height: 10),
          SizedBox(
            width: double.infinity,
            child: FilledButton.icon(
              onPressed: _parsing ? null : _parse,
              icon: _parsing
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.search, size: 20),
              label: Text(_parsing ? '解析中…' : '解析分享'),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildError(String error) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.fromLTRB(16, 4, 16, 8),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: scheme.errorContainer.withOpacity(0.5),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.error_outline, size: 18, color: scheme.error),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              error,
              style: TextStyle(fontSize: 13, color: scheme.onErrorContainer),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildBreadcrumb(AppController controller) {
    final browse = controller.browse;
    final scheme = Theme.of(context).colorScheme;
    final crumbs = <({String fid, String name})>[
      (fid: '0', name: browse.title),
      ...browse.breadcrumbs,
    ];

    return SizedBox(
      height: 36,
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: 16),
        itemCount: crumbs.length,
        separatorBuilder: (_, __) => Icon(
          Icons.chevron_right,
          size: 16,
          color: scheme.onSurfaceVariant,
        ),
        itemBuilder: (context, index) {
          final crumb = crumbs[index];
          final isLast = index == crumbs.length - 1;
          return Center(
            child: Text(
              crumb.name,
              style: TextStyle(
                fontSize: 13,
                fontWeight: isLast ? FontWeight.w600 : FontWeight.normal,
                color: isLast ? scheme.onSurface : scheme.primary,
              ),
            ),
          );
        },
      ),
    );
  }

  Widget _buildBody(AppController controller) {
    final browse = controller.browse;

    if (browse.loading) {
      return const Center(child: CircularProgressIndicator());
    }

    if (browse.pwdId.isEmpty) {
      return _buildEmpty();
    }

    if (browse.entries.isEmpty) {
      return const Center(
        child: Text('这个目录是空的', style: TextStyle(fontSize: 14)),
      );
    }

    return RefreshIndicator(
      onRefresh: () async {
        if (browse.breadcrumbs.isEmpty) {
          await controller.openShare(controller.settings.lastShareUrl);
        } else {
          await controller.goUp();
        }
      },
      child: ListView.separated(
        controller: _scrollController,
        padding: const EdgeInsets.only(bottom: 24),
        itemCount: browse.entries.length + (browse.hasMore ? 1 : 0),
        separatorBuilder: (_, __) => const Divider(height: 1, indent: 56),
        itemBuilder: (context, index) {
          if (index >= browse.entries.length) {
            return const Padding(
              padding: EdgeInsets.symmetric(vertical: 20),
              child: Center(
                child: SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ),
            );
          }
          final entry = browse.entries[index];
          return _buildEntry(controller, entry);
        },
      ),
    );
  }

  Widget _buildEntry(AppController controller, QuarkFileEntry entry) {
    final scheme = Theme.of(context).colorScheme;

    return ListTile(
      leading: Icon(
        entry.isDir ? Icons.folder : _iconForFile(entry.name),
        color: entry.isDir ? scheme.primary : scheme.onSurfaceVariant,
        size: 26,
      ),
      title: Text(
        entry.name,
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: const TextStyle(fontSize: 14),
      ),
      subtitle: entry.isDir
          ? null
          : Text(
              Formatters.bytes(entry.size),
              style: TextStyle(fontSize: 12, color: scheme.onSurfaceVariant),
            ),
      trailing: entry.isDir
          ? Icon(Icons.chevron_right, color: scheme.onSurfaceVariant)
          : IconButton(
              tooltip: '下载',
              icon: const Icon(Icons.download),
              onPressed: () => _download(controller, entry),
            ),
      onTap: entry.isDir
          ? () => controller.enterDirectory(entry)
          : () => _download(controller, entry),
    );
  }

  Future<void> _download(AppController controller, QuarkFileEntry entry) async {
    final error = await controller.downloadFile(entry);
    if (!mounted) return;
    _toast(error ?? '已加入下载队列：${entry.name}');
  }

  IconData _iconForFile(String name) {
    final lower = name.toLowerCase();
    if (lower.endsWith('.epub') || lower.endsWith('.mobi') ||
        lower.endsWith('.azw3') || lower.endsWith('.pdf')) {
      return Icons.menu_book;
    }
    if (lower.endsWith('.jpg') || lower.endsWith('.png') ||
        lower.endsWith('.gif') || lower.endsWith('.webp')) {
      return Icons.image;
    }
    if (lower.endsWith('.mp4') || lower.endsWith('.mkv') ||
        lower.endsWith('.avi') || lower.endsWith('.mov')) {
      return Icons.movie;
    }
    if (lower.endsWith('.mp3') || lower.endsWith('.flac') ||
        lower.endsWith('.m4a') || lower.endsWith('.wav')) {
      return Icons.audiotrack;
    }
    if (lower.endsWith('.zip') || lower.endsWith('.rar') ||
        lower.endsWith('.7z') || lower.endsWith('.tar') ||
        lower.endsWith('.gz')) {
      return Icons.folder_zip;
    }
    return Icons.insert_drive_file;
  }

  Widget _buildEmpty() {
    final scheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              Icons.cloud_download_outlined,
              size: 64,
              color: scheme.onSurfaceVariant.withOpacity(0.4),
            ),
            const SizedBox(height: 16),
            Text(
              '还没有内容',
              style: TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w500,
                color: scheme.onSurface,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              '在上方粘贴夸克分享链接\n游客即可解析和下载，无需登录',
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
