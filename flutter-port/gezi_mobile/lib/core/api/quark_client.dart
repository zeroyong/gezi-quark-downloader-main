import '../model/quark_file_entry.dart';
import '../net/cookie_jar.dart';
import '../net/quark_constants.dart';
import '../net/quark_session.dart';
import '../util/link_quality.dart';

/// 夸克网盘 API 客户端。
///
/// 移植自桌面版 `GeZi.Core/Api/QuarkClient.cs`（1106 行）。
/// 这里只保留**下载器真正需要的**接口：
///   分享 token → 列目录 → 取直链
/// （我的网盘、转存、删除、回收站等桌面版功能暂不需要，故不移植。）
///
/// 所有接口路径与参数均已用真实分享链接验证通过。
class QuarkClient {
  /// 会话。**取链与下载必须共用同一个实例**（Cookie 会话绑定，见 cookie_jar.dart）。
  final QuarkSession session;

  QuarkClient({String? cookie, QuarkSession? session})
      : session = session ?? QuarkSession(cookie: cookie);

  /// 是否已登录。
  bool get isLoggedIn => session.jar.hasLogin;

  /// 当前 Cookie 串（用于持久化）。
  String get cookieString => session.jar.toHeaderValue();

  // ==========================================================================
  // 分享
  // ==========================================================================

  /// 换取分享 stoken。
  ///
  /// 游客也能调用（不需要登录 Cookie），这是本方案**不强制登录**的基础。
  Future<ShareTokenResult> getShareToken(
    String pwdId, {
    String passcode = '',
    CancellationToken? cancel,
  }) async {
    final res = await session.postJson(
      '${QuarkConstants.apiBase}/1/clouddrive/share/sharepage/token'
      '?${QuarkConstants.commonQuery}',
      <String, dynamic>{'pwd_id': pwdId, 'passcode': passcode},
      cancel: cancel,
    );
    _check(res, '获取分享信息');

    final data = _asMap(_get(res, 'data'));
    final stoken = (data?['stoken'] ?? '').toString();
    if (stoken.isEmpty) {
      throw const QuarkApiException('分享已失效，或提取码错误');
    }

    return ShareTokenResult(
      stoken: stoken,
      title: (data?['title'] ?? '').toString(),
      authorName: _asMap(data?['author'])?['nick_name']?.toString() ?? '',
      expiredAt: (data?['expired_at'] as num?)?.toInt() ?? 0,
    );
  }

  /// 列分享目录（分页）。
  ///
  /// [pdirFid] 传 `'0'` 表示根目录。
  Future<DirPage> listShareDir({
    required String pwdId,
    required String stoken,
    String pdirFid = '0',
    int page = 1,
    int pageSize = 50,
    CancellationToken? cancel,
  }) async {
    final url = '${QuarkConstants.apiBase}/1/clouddrive/share/sharepage/detail'
        '?${QuarkConstants.commonQuery}'
        '&pwd_id=$pwdId'
        '&stoken=${Uri.encodeComponent(stoken)}'
        '&pdir_fid=$pdirFid'
        '&force=0'
        '&_page=$page'
        '&_size=$pageSize'
        // banner/share 详情只在首页需要，关掉能省流量
        '&_fetch_banner=0'
        '&_fetch_share=1'
        '&_fetch_total=1'
        '&_sort=file_type:asc,updated_at:desc';

    final res = await session.getJson(url, cancel: cancel);
    _check(res, '读取目录');

    final data = _asMap(_get(res, 'data'));
    final rawList = data?['list'];
    final items = <QuarkFileEntry>[];
    if (rawList is List) {
      for (final item in rawList) {
        final map = _asMap(item);
        if (map != null) items.add(QuarkFileEntry.fromJson(map));
      }
    }

    final total = (_asMap(_get(res, 'metadata'))?['_total'] as num?)?.toInt() ??
        items.length;

    return DirPage(items: items, total: total, page: page);
  }

  // ==========================================================================
  // 取直链
  // ==========================================================================

  /// 取「我的网盘」直链。
  Future<List<DownloadLink>> getMyDriveDownload(
    List<String> fids, {
    CancellationToken? cancel,
  }) {
    final attempts = <Map<String, String>>[
      // 入口 1：PC 客户端入口（常规最优）
      <String, String>{'pr': 'ucpro', 'fr': 'pc', 'uc_param_str': ''},
      // 入口 2：网页端入口（CDN 选择策略可能不同）
      <String, String>{
        'pr': 'ucpro',
        'fr': 'pc',
        'uc_param_str': '',
        'entry': 'ft',
      },
      // 入口 3：移动端入口（部分网络下反而更稳）
      <String, String>{'pr': 'ucpro', 'fr': 'android', 'uc_param_str': ''},
    ];
    return _fetchBestLinks(
      attempts: attempts,
      body: <String, dynamic>{'fids': fids},
      cancel: cancel,
    );
  }

  /// 取「分享」直链（免转存，不占网盘空间）。
  ///
  /// [shareTokens] 与 [fids] 一一对应，来自 [QuarkFileEntry.shareToken]。
  Future<List<DownloadLink>> getShareDownload(
    List<String> fids,
    List<String?> shareTokens, {
    required String pwdId,
    required String stoken,
    CancellationToken? cancel,
  }) {
    final attempts = <Map<String, String>>[
      <String, String>{'entry': 'ft', 'fr': 'pc', 'pr': 'ucpro'},
      <String, String>{'pr': 'ucpro', 'fr': 'pc', 'uc_param_str': ''},
      <String, String>{'pr': 'ucpro', 'fr': 'android', 'uc_param_str': ''},
    ];
    return _fetchBestLinks(
      attempts: attempts,
      body: <String, dynamic>{
        'fids': fids,
        'fids_token': shareTokens,
        'pwd_id': pwdId,
        'stoken': stoken,
      },
      cancel: cancel,
    );
  }

  /// 把多个入口都问一遍，再用 [LinkQuality] 择优。
  ///
  /// 多入口的意义：不同入口返回的直链对应不同 CDN 节点与签名，
  /// 择优能明显减少下载中途的 403/断流。
  /// 只在「全部入口都失败」时才抛异常，单个入口失败不影响整体。
  Future<List<DownloadLink>> _fetchBestLinks({
    required List<Map<String, String>> attempts,
    required Map<String, dynamic> body,
    CancellationToken? cancel,
  }) async {
    final candidates = <List<DownloadLink>>[];
    String? lastError;

    for (final query in attempts) {
      cancel?.throwIfCancelled();
      try {
        final qs = query.entries
            .map((e) => '${e.key}=${Uri.encodeComponent(e.value)}')
            .join('&');
        final res = await session.postJson(
          '${QuarkConstants.apiBase}/1/clouddrive/file/download?$qs',
          body,
          cancel: cancel,
        );
        _check(res, '获取直链');

        final links = _parseLinks(_get(res, 'data'));
        if (links.isNotEmpty) candidates.add(links);
      } on QuarkApiException catch (e) {
        lastError = e.message;
      }
    }

    if (candidates.isEmpty) {
      throw QuarkApiException(lastError ?? '获取直链失败：所有入口均无响应');
    }

    // 择优：总分最高的那组
    final best = LinkQuality.pickBest<DownloadLink>(candidates, (l) => l.url);
    return best ?? candidates.first;
  }

  List<DownloadLink> _parseLinks(dynamic data) {
    final links = <DownloadLink>[];
    if (data is! List) return links;
    for (final item in data) {
      final map = _asMap(item);
      if (map == null) continue;
      final url = (map['download_url'] ?? '').toString();
      if (url.isEmpty) continue;
      links.add(DownloadLink(
        fid: (map['fid'] ?? '').toString(),
        url: url,
        name: (map['file_name'] ?? '').toString(),
        size: (map['size'] as num?)?.toInt() ?? 0,
      ));
    }
    return links;
  }

  // ==========================================================================
  // 内部工具
  // ==========================================================================

  /// 校验接口响应的 `status`/`code`。
  ///
  /// 夸克的成功标志是 `status == 200 && code == 0`。
  /// 注意：有些接口成功时 `code` 是字符串 `"OK"`，所以这里只把
  /// **数字且非 0** 的 code 视为失败。
  static void _check(dynamic res, String what) {
    final map = _asMap(res);
    if (map == null) {
      throw QuarkApiException('$what失败：响应不是 JSON 对象');
    }

    final status = (map['status'] as num?)?.toInt();
    if (status != null && status != 200) {
      final msg = (map['message'] ?? '未知错误').toString();
      throw QuarkApiException('$what失败：$msg', code: status);
    }

    final code = map['code'];
    if (code is num && code != 0) {
      final msg = (map['message'] ?? '未知错误').toString();
      throw QuarkApiException('$what失败：$msg', code: code.toInt());
    }
  }

  static dynamic _get(dynamic map, String key) {
    final m = _asMap(map);
    return m?[key];
  }

  static Map<String, dynamic>? _asMap(dynamic value) {
    if (value is Map<String, dynamic>) return value;
    if (value is Map) return value.cast<String, dynamic>();
    return null;
  }

  void close() => session.close();
}

/// 分享信息。
class ShareTokenResult {
  final String stoken;
  final String title;
  final String authorName;

  /// 过期时间（毫秒时间戳，0 表示未知/永久）。
  final int expiredAt;

  const ShareTokenResult({
    required this.stoken,
    this.title = '',
    this.authorName = '',
    this.expiredAt = 0,
  });
}
