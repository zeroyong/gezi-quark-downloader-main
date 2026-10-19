/// 网盘文件条目。
///
/// 对应桌面版 `GeZi.Core/Models/QuarkFileEntry.cs`。
class QuarkFileEntry {
  final String fid;
  final String name;
  final int size;
  final bool isDir;

  /// 分享内文件的独立 token（`share_fid_token`）。
  /// 只有分享模式有，取分享直链时必须带上。
  final String? shareToken;

  const QuarkFileEntry({
    required this.fid,
    required this.name,
    required this.size,
    required this.isDir,
    this.shareToken,
  });

  factory QuarkFileEntry.fromJson(Map<String, dynamic> json) {
    return QuarkFileEntry(
      fid: (json['fid'] ?? '').toString(),
      name: (json['file_name'] ?? json['name'] ?? '').toString(),
      size: _asInt(json['size']),
      isDir: json['dir'] == true || json['file_type'] == 0,
      shareToken: json['share_fid_token']?.toString(),
    );
  }

  static int _asInt(dynamic v) {
    if (v == null) return 0;
    if (v is int) return v;
    if (v is num) return v.toInt();
    return int.tryParse(v.toString()) ?? 0;
  }

  Map<String, dynamic> toJson() => <String, dynamic>{
        'fid': fid,
        'file_name': name,
        'size': size,
        'dir': isDir,
        if (shareToken != null) 'share_fid_token': shareToken,
      };

  @override
  String toString() => '${isDir ? "[目录]" : "[文件]"} $name ($size B)';
}

/// 一条下载直链。
///
/// 对应桌面版 `GeZi.Core/Models/DownloadLink.cs`。
class DownloadLink {
  final String fid;
  final String url;
  final String name;
  final int size;

  const DownloadLink({
    required this.fid,
    required this.url,
    required this.name,
    required this.size,
  });

  @override
  String toString() => 'DownloadLink($name, $size B)';
}

/// 分页目录结果。
class DirPage {
  final List<QuarkFileEntry> items;

  /// 服务端声明的总数（可能大于 [items].length，表示还有下一页）。
  final int total;

  /// 当前页码（从 1 开始）。
  final int page;

  const DirPage({
    required this.items,
    required this.total,
    this.page = 1,
  });

  bool get hasMore => items.isNotEmpty && page * items.length < total;
}
