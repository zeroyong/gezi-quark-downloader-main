// GeZi Flutter 移植 —— 夸克 API 客户端
//
// 对应 C# 的 GeZi.Core/Api/QuarkClient.cs（1106 行）
// 这里覆盖「解析分享 → 列目录 → 取直链」这条主链路，
// 接口路径与参数均已用真实分享链接验证通过。

import 'quark_session.dart';

/// 网盘文件条目（对应 QuarkFileEntry.cs）
class QuarkFileEntry {
  final String fid;
  final String name;
  final int size;
  final bool isDir;
  final String? token; // share_fid_token

  const QuarkFileEntry({
    required this.fid,
    required this.name,
    required this.size,
    required this.isDir,
    this.token,
  });

  factory QuarkFileEntry.fromJson(Map<String, dynamic> j) => QuarkFileEntry(
        fid: j['fid'] as String? ?? '',
        name: j['file_name'] as String? ?? '',
        size: (j['size'] as num?)?.toInt() ?? 0,
        isDir: j['dir'] as bool? ?? false,
        token: j['share_fid_token'] as String?,
      );
}

/// 直链（对应 DownloadLink.cs）
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
}

/// 分页结果（对应 DirPage）
class DirPage {
  final List<QuarkFileEntry> items;
  final int total;
  const DirPage(this.items, this.total);
}

class QuarkClient {
  final QuarkSession session;

  QuarkClient({String? cookie}) : session = QuarkSession(cookie: cookie);

  /// 是否已登录（关键 Cookie __puus）
  bool get isLoggedIn => session.jar.has('__puus') || session.jar.has('__pus');

  static const _q = 'pr=ucpro&fr=pc&uc_param_str=';

  // ---------------- 分享解析 ----------------

  /// 换取分享 stoken。游客也能调（不需要登录 Cookie）。
  Future<String> getStoken(String pwdId, {String passcode = ''}) async {
    final res = await session.postJson(
      '${QuarkConstants.apiBase}/1/clouddrive/share/sharepage/token?$_q',
      {'pwd_id': pwdId, 'passcode': passcode},
    );
    _check(res, '获取分享 token');
    final stoken = res['data']?['stoken'] as String?;
    if (stoken == null || stoken.isEmpty) {
      throw QuarkApiException('分享 token 为空，可能已失效或提取码错误');
    }
    return stoken;
  }

  /// 分享标题等信息
  Future<Map<String, dynamic>> getShareInfo(String pwdId, {String passcode = ''}) async {
    final res = await session.postJson(
      '${QuarkConstants.apiBase}/1/clouddrive/share/sharepage/token?$_q',
      {'pwd_id': pwdId, 'passcode': passcode},
    );
    _check(res, '获取分享信息');
    return (res['data'] as Map).cast<String, dynamic>();
  }

  /// 列分享目录（分页）。[pdirFid] 为 '0' 表示根目录。
  Future<DirPage> shareDetailPage(
    String pwdId,
    String stoken,
    String pdirFid, {
    int page = 1,
    int size = 50,
  }) async {
    final url = '${QuarkConstants.apiBase}/1/clouddrive/share/sharepage/detail?$_q'
        '&pwd_id=$pwdId'
        '&stoken=${Uri.encodeComponent(stoken)}'
        '&pdir_fid=$pdirFid'
        '&force=0&_page=$page&_size=$size'
        '&_fetch_banner=0&_fetch_share=1&_fetch_total=1'
        '&_sort=file_type:asc,updated_at:desc';
    final res = await session.getJson(url);
    _check(res, '列目录');
    final list = (res['data']?['list'] as List?) ?? const [];
    final items = list
        .whereType<Map>()
        .map((e) => QuarkFileEntry.fromJson(e.cast<String, dynamic>()))
        .toList();
    final total = (res['metadata']?['_total'] as num?)?.toInt() ?? items.length;
    return DirPage(items, total);
  }

  // ---------------- 取直链 ----------------

  /// 取「我的网盘」直链。
  /// 策略与桌面版一致：把所有官方入口都问一遍，再按质量分择优 ——
  /// 不同入口对应不同 CDN 节点与签名，择优能明显减少中途 403/断流。
  Future<List<DownloadLink>> getDownload(List<String> fids) async {
    final attempts = <Map<String, String>>[
      {'pr': 'ucpro', 'fr': 'pc', 'uc_param_str': ''},
      {'pr': 'ucpro', 'fr': 'pc', 'uc_param_str': '', 'entry': 'ft'},
      {'pr': 'ucpro', 'fr': 'android', 'uc_param_str': ''},
    ];
    return _pickBest(attempts, {'fids': fids});
  }

  /// 取「分享」直链（免转存，不占网盘空间）
  Future<List<DownloadLink>> getShareDownload(
    List<String> fids,
    List<String?> tokens, {
    required String pwdId,
    required String stoken,
  }) async {
    final attempts = <Map<String, String>>[
      {'entry': 'ft', 'fr': 'pc', 'pr': 'ucpro'},
      {'pr': 'ucpro', 'fr': 'pc', 'uc_param_str': ''},
      {'pr': 'ucpro', 'fr': 'android', 'uc_param_str': ''},
    ];
    return _pickBest(attempts, {
      'fids': fids,
      'fids_token': tokens,
      'pwd_id': pwdId,
      'stoken': stoken,
    });
  }

  Future<List<DownloadLink>> _pickBest(
    List<Map<String, String>> attempts,
    Map<String, dynamic> body,
  ) async {
    List<DownloadLink>? best;
    List<DownloadLink>? last;
    String? lastErr;

    for (final q in attempts) {
      try {
        final qs = q.entries.map((e) => '${e.key}=${Uri.encodeComponent(e.value)}').join('&');
        final res = await session.postJson(
          '${QuarkConstants.apiBase}/1/clouddrive/file/download?$qs',
          body,
        );
        _check(res, '获取直链');
        final list = _parseLinks(res['data']);
        last = list;
        if (list.isNotEmpty && list.any((l) => l.url.length > 20)) {
          if (best == null || _totalScore(list) > _totalScore(best)) best = list;
        }
      } on QuarkApiException catch (e) {
        lastErr = e.message;
      }
    }
    if (best != null) return best;
    if (last != null && last.isNotEmpty) return last;
    throw QuarkApiException(lastErr ?? '获取直链失败');
  }

  List<DownloadLink> _parseLinks(dynamic data) {
    if (data is! List) return const [];
    return data.whereType<Map>().map((e) {
      final m = e.cast<String, dynamic>();
      return DownloadLink(
        fid: m['fid'] as String? ?? '',
        url: m['download_url'] as String? ?? '',
        name: m['file_name'] as String? ?? '',
        size: (m['size'] as num?)?.toInt() ?? 0,
      );
    }).where((l) => l.url.isNotEmpty).toList();
  }

  // ---------------- 直链质量打分（对应 LinkQuality.cs）----------------

  /// 同一个文件从不同入口取回的直链，CDN 节点/签名有效期/限速策略都不同。
  /// 这里用纯本地启发式打分排序，不发额外请求、不触碰风控边界。
  static int score(String url) {
    if (url.isEmpty || url.length <= 20) return -1;
    var s = 0;
    if (url.startsWith('https://')) s += 100;
    if (url.contains('auth_key=')) s += 30;
    if (url.contains('Expires=')) s += 15;
    if (url.contains('Signature=')) s += 15;
    if (url.contains('.quark.cn')) s += 10;
    if (url.contains('.uc.cn')) s += 6;
    if (url.contains('cdn')) s += 5;
    final low = url.toLowerCase();
    if (low.contains('error') || low.contains('denied') || low.contains('forbidden')) s -= 200;
    if (url.length < 60) s -= 20;
    return s;
  }

  static int _totalScore(List<DownloadLink> l) =>
      l.fold(0, (a, e) => a + (score(e.url) < 0 ? 0 : score(e.url)));

  // ---------------- 校验 ----------------

  static void _check(dynamic res, String what) {
    if (res is! Map) throw QuarkApiException('$what: 响应不是 JSON 对象');
    final status = (res['status'] as num?)?.toInt() ?? -1;
    final code = (res['code'] as num?)?.toInt() ?? -1;
    if (status != 200 || code != 0) {
      final msg = res['message'] as String? ?? '未知错误';
      throw QuarkApiException('$what失败: $msg', code: code);
    }
  }

  void close() => session.close();
}
