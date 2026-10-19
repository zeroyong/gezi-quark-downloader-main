// GeZi Flutter 移植 —— 会话层
//
// 对应 C# 的 GeZi.Core/Support/QuarkSession.cs
//
// 【移植要点 / 踩过的坑】
// 1. Dart 的 HttpClient **不会自动管理 Cookie**（没有 CookieContainer 对应物）。
//    必须自己维护 jar 并每请求手动附加，否则 CDN 直链会直接 412。
//    这是实测确认的：同一 URL 带 Cookie → 206，不带 → 412。
// 2. dart:io 的 TLS 栈基于 BoringSSL，TLS 1.3 + X25519 齐全，
//    **可以直连 pan.quark.cn** —— 桌面版为此内置了 20MB CPython 来绕过
//    .NET SChannel 的 ClientHello 指纹问题，移动端完全不需要。
// 3. 不要用 http 包的默认 client 来做重定向/Cookie 管理，行为与桌面版不一致。

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

/// 夸克接口常量（对应 QuarkConstants.cs）
class QuarkConstants {
  static const apiBase = 'https://drive-pc.quark.cn';
  static const accountUrl = 'https://pan.quark.cn/account/info';
  static const referer = 'https://pan.quark.cn/';
  static const origin = 'https://pan.quark.cn';

  /// 伪装成夸克 PC 客户端（Electron）。取链与下载必须用同一族 UA，
  /// 否则 CDN 的一致性校验会判定盗链。
  static const ua =
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 '
      '(KHTML, like Gecko) quark-cloud-drive/3.14.2 Chrome/112.0.5615.165 '
      'Electron/24.1.3.8 Safari/537.36 Channel/pckk_other_ch';

  static const qrGetToken = 'https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin';
  static const qrGetTicket = 'https://uop.quark.cn/cas/ajax/getServiceTicketByQrcodeToken';
  static const qrScanBase = 'https://su.quark.cn/4_eMHBJ';

  static const qrStatusOk = 2000000;
  static const qrWaitingScan = 50004001;
  static const qrTokenNotFound = 50004002;
  static const qrTokenExpired = 50004003;

  /// 单次读取缓冲
  static const downloadChunk = 256 * 1024;

  /// 小于此值不分片
  static const minSegment = 4 * 1024 * 1024;
}

/// 夸克接口异常
class QuarkApiException implements Exception {
  final String message;
  final int? httpStatus;
  final int? code;
  QuarkApiException(this.message, {this.httpStatus, this.code});
  @override
  String toString() => 'QuarkApiException($httpStatus/$code): $message';
}

/// 手写 Cookie 罐。
///
/// 为什么不用 cookie_jar 包：夸克的 Cookie 全部落在 .quark.cn 域，
/// 只需要 name→value，不需要按 path/domain 精细匹配。手写 20 行更可控，
/// 且能保证「服务端 Set-Cookie 立即生效」（桌面版注释里强调过这点）。
class CookieJar {
  final Map<String, String> _map = {};

  void absorb(HttpClientResponse resp) {
    final setCookies = resp.headers['set-cookie'];
    if (setCookies == null) return;
    for (final line in setCookies) {
      // 一条 Set-Cookie 可能是 "a=1; Path=/; Domain=.quark.cn; HttpOnly"
      final first = line.split(';').first;
      final eq = first.indexOf('=');
      if (eq <= 0) continue;
      final k = first.substring(0, eq).trim();
      final v = first.substring(eq + 1).trim();
      if (k.isNotEmpty) _map[k] = v;
    }
  }

  void seed(String cookieString) {
    for (final part in cookieString.split(';')) {
      final p = part.trim();
      if (p.isEmpty) continue;
      final eq = p.indexOf('=');
      if (eq <= 0) continue;
      _map[p.substring(0, eq).trim()] = p.substring(eq + 1).trim();
    }
  }

  String? operator [](String name) => _map[name];
  bool has(String name) => _map.containsKey(name);
  Iterable<String> get keys => _map.keys;

  /// 拼成下发用的 Cookie 头
  String toHeader() => _map.entries.map((e) => '${e.key}=${e.value}').join('; ');

  Map<String, String> toMap() => Map.unmodifiable(_map);
  void clear() => _map.clear();
}

/// 出站会话：统一请求头指纹、Cookie 载体、节流、退避重试。
class QuarkSession {
  final CookieJar jar = CookieJar();
  late final HttpClient _http;
  final Random _rng = Random();

  /// 两次请求之间的最小间隔（毫秒）。过低会被视为机器流量。
  int minIntervalMs = 260;
  DateTime _lastRequestAt = DateTime.fromMillisecondsSinceEpoch(0);

  QuarkSession({String? cookie}) {
    _http = HttpClient()
      ..connectionTimeout = const Duration(seconds: 20)
      ..idleTimeout = const Duration(seconds: 15)
      ..maxConnectionsPerHost = 64; // 移动端不要开 512，见下方说明
    if (cookie != null && cookie.isNotEmpty) jar.seed(cookie);
  }

  /// 构造一套完整自洽的浏览器请求头。
  /// 「完整」是关键：真实浏览器不会只带 UA，缺项本身就是可疑特征。
  Map<String, String> headers({bool api = true, bool json = false}) {
    final h = <String, String>{
      'User-Agent': QuarkConstants.ua,
      'Accept': api ? 'application/json, text/plain, */*' : '*/*',
      'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
      'Referer': QuarkConstants.referer,
      'Origin': QuarkConstants.origin,
      'Cache-Control': 'no-cache',
      'Pragma': 'no-cache',
      'Connection': 'keep-alive',
      'Sec-Fetch-Dest': api ? 'empty' : 'document',
      'Sec-Fetch-Mode': api ? 'cors' : 'navigate',
      'Sec-Fetch-Site': 'same-site',
    };
    if (json) h['Content-Type'] = 'application/json';
    final c = jar.toHeader();
    if (c.isNotEmpty) h['Cookie'] = c;
    return h;
  }

  /// 节流：保证两次请求间隔 ≥ minIntervalMs（带随机抖动，避免固定机器节奏）
  Future<void> throttle([CancellationToken? cancel]) async {
    final now = DateTime.now();
    final elapsed = now.difference(_lastRequestAt).inMilliseconds;
    final jitter = 0.75 + 0.5 * _rng.nextDouble();
    final need = (minIntervalMs * jitter - elapsed).round();
    if (need > 0) {
      await Future.delayed(Duration(milliseconds: need));
      _lastRequestAt = DateTime.now();
    } else {
      _lastRequestAt = now;
    }
  }

  bool _isRetryable(int code) => code == 408 || code == 429 || (code >= 500 && code < 600);

  /// 带指数退避的请求。5xx/408/429 会重试，退避带抖动避免所有分片同时重试。
  Future<HttpClientResponse> sendWithRetry(
    Future<HttpClientRequest> Function(HttpClient client) build, {
    int maxAttempts = 3,
    CancellationToken? cancel,
    void Function(int status)? onRetry,
  }) async {
    Object? last;
    for (var attempt = 0; attempt < maxAttempts; attempt++) {
      cancel?.throwIfCancelled();
      await throttle(cancel);
      try {
        final req = await build(_http);
        final resp = await req.close();
        if (_isRetryable(resp.statusCode) && attempt < maxAttempts - 1) {
          onRetry?.call(resp.statusCode);
          await resp.drain<void>();
          await _backoff(attempt);
          continue;
        }
        jar.absorb(resp);
        return resp;
      } catch (e) {
        last = e;
        if (attempt < maxAttempts - 1) {
          await _backoff(attempt);
          continue;
        }
      }
    }
    throw QuarkApiException('请求失败(已重试 $maxAttempts 次): $last');
  }

  Future<void> _backoff(int attempt) async {
    final baseMs = 600 * pow(2.2, attempt);
    final jitter = 0.6 + 0.8 * _rng.nextDouble();
    final ms = min((baseMs * jitter).round(), 15000);
    await Future.delayed(Duration(milliseconds: ms));
  }

  // ---------------- JSON 便捷方法 ----------------

  Future<dynamic> getJson(String url, {int timeoutSeconds = 30}) async {
    final resp = await sendWithRetry((client) async {
      final req = await client.getUrl(Uri.parse(url));
      headers().forEach(req.headers.set);
      return req;
    });
    final text = await resp.transform(utf8.decoder).join();
    if (resp.statusCode >= 400) {
      throw QuarkApiException(_authHint(resp.statusCode) ?? 'HTTP ${resp.statusCode}',
          httpStatus: resp.statusCode);
    }
    return jsonDecode(text);
  }

  Future<dynamic> postJson(String url, Object body, {int timeoutSeconds = 30}) async {
    final resp = await sendWithRetry((client) async {
      final req = await client.postUrl(Uri.parse(url));
      headers(json: true).forEach(req.headers.set);
      req.add(utf8.encode(jsonEncode(body)));
      return req;
    });
    final text = await resp.transform(utf8.decoder).join();
    if (resp.statusCode >= 400) {
      throw QuarkApiException(_authHint(resp.statusCode) ?? 'HTTP ${resp.statusCode}',
          httpStatus: resp.statusCode);
    }
    return jsonDecode(text);
  }

  static String? _authHint(int code) => switch (code) {
        401 => '登录态已失效(401)，请重新登录',
        403 => '访问被拒绝(403)，可能登录态失效或权限不足',
        412 => '请求被拦截(412)，请稍后重试；持续出现请重新登录',
        429 => '请求过于频繁(429)，已自动退避',
        _ => null,
      };

  void close() => _http.close(force: true);
}

/// 极简取消令牌（对应 C# 的 CancellationToken）。
/// Dart 没有内建取消机制，而下载器到处需要「暂停/取消」，
/// 所以统一用这个，避免每处自己写 bool 标志。
class CancellationToken {
  bool _cancelled = false;
  final List<void Function()> _listeners = [];

  bool get isCancelled => _cancelled;

  void cancel() {
    if (_cancelled) return;
    _cancelled = true;
    for (final l in List.of(_listeners)) {
      l();
    }
    _listeners.clear();
  }

  void throwIfCancelled() {
    if (_cancelled) throw const CancelledException();
  }

  void onCancel(void Function() cb) {
    if (_cancelled) {
      cb();
    } else {
      _listeners.add(cb);
    }
  }
}

class CancelledException implements Exception {
  const CancelledException();
  @override
  String toString() => 'CancelledException';
}

/// 暂停令牌（对应 PauseToken.cs）
class PauseToken {
  bool _paused = false;
  bool get isPaused => _paused;
  void pause() => _paused = true;
  void resume() => _paused = false;

  Future<void> waitIfPaused(CancellationToken cancel) async {
    while (_paused) {
      cancel.throwIfCancelled();
      await Future.delayed(const Duration(milliseconds: 100));
    }
  }
}
