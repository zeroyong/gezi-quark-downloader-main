import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'cookie_jar.dart';
import 'quark_constants.dart';

/// 出站请求的会话层。
///
/// 移植自桌面版 `GeZi.Core/Support/QuarkSession.cs`。
///
/// 职责：
///   · 统一请求头指纹（让请求看起来像真实客户端，而不是缺项的可疑流量）
///   · Cookie 载体（**贯穿取链到下载，缺了必 412**，见 cookie_jar.dart）
///   · 请求节流（避免固定频率的机器节奏）
///   · 指数退避重试（5xx / 408 / 429）
class QuarkSession {
  /// Cookie 罐。**整个生命周期内必须是同一个实例**。
  final CookieJar jar;

  late final HttpClient _http;
  final Random _rng = Random();

  /// 两次请求之间的最小间隔（毫秒）。
  ///
  /// 260ms 来自桌面版实测：过低会被服务端视为机器流量，
  /// 过高会明显拖慢多接口串行调用（一次解析要连打 3~4 个接口）。
  int minIntervalMs;

  DateTime _lastRequestAt = DateTime.fromMillisecondsSinceEpoch(0);

  QuarkSession({
    String? cookie,
    this.minIntervalMs = 260,
    HttpClient? httpClient,
  }) : jar = CookieJar() {
    if (cookie != null && cookie.isNotEmpty) {
      jar.seedFromString(cookie);
    }
    _http = httpClient ?? _buildClient();
  }

  HttpClient _buildClient() {
    return HttpClient()
      // 连接超时不能太短：移动网络首次 TLS 握手可能要 3~5 秒
      ..connectionTimeout = const Duration(seconds: 20)
      // 空闲连接保持时间。太短会频繁重握手，太长会占住连接配额。
      ..idleTimeout = const Duration(seconds: 15)
      // 【移动端关键】桌面版是 512，手机上必须大幅下调。
      // 蜂窝网络 NAT 会话表小、夸克按账号限连接、SoC 功耗敏感。
      // 实测 16~32 路足以跑满 4G/5G（单连接速本就只有 40~200KB/s）。
      ..maxConnectionsPerHost = 32
      // 不用系统代理：代理会改变 TLS 指纹，且移动端代理行为不可控
      ..findProxy = ((Uri uri) => 'DIRECT');
  }

  /// 构造一套完整、自洽的浏览器请求头。
  ///
  /// 「完整」是重点：真实浏览器不会只带 UA 和 Referer，
  /// 缺项（尤其是 Sec-Fetch-*）本身就是明显的非浏览器特征。
  ///
  /// [api] true 表示调用 JSON 接口，false 表示下载直链。
  Map<String, String> buildHeaders({bool api = true, bool json = false}) {
    final headers = <String, String>{
      'User-Agent': QuarkConstants.userAgent,
      'Accept': api ? 'application/json, text/plain, */*' : '*/*',
      'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
      'Referer': QuarkConstants.referer,
      'Origin': QuarkConstants.origin,
      'Cache-Control': 'no-cache',
      'Pragma': 'no-cache',
      'DNT': '1',
      'Sec-Fetch-Dest': api ? 'empty' : 'document',
      'Sec-Fetch-Mode': api ? 'cors' : 'navigate',
      'Sec-Fetch-Site': 'same-site',
    };
    if (json) headers['Content-Type'] = 'application/json';
    final cookie = jar.toHeaderValue();
    if (cookie.isNotEmpty) headers['Cookie'] = cookie;
    return headers;
  }

  /// 节流：保证两次请求间隔 ≥ [minIntervalMs]（带 ±25% 随机抖动）。
  ///
  /// 抖动的作用是避免「固定 260ms 一次」这种完美周期 —— 那本身就是
  /// 机器特征。真实用户的请求间隔是散乱的。
  Future<void> throttle() async {
    final now = DateTime.now();
    final elapsed = now.difference(_lastRequestAt).inMilliseconds;
    final jitter = 0.75 + 0.5 * _rng.nextDouble();
    final needMs = (minIntervalMs * jitter - elapsed).round();
    if (needMs > 0) {
      await Future<void>.delayed(Duration(milliseconds: needMs));
      _lastRequestAt = DateTime.now();
    } else {
      _lastRequestAt = now;
    }
  }

  static bool _isRetryableStatus(int code) =>
      code == 408 || code == 429 || (code >= 500 && code < 600);

  /// 带指数退避的请求执行。
  ///
  /// 退避时间带随机抖动：所有分片若在同一时刻重试会形成尖峰，
  /// 既打不进去，又容易被判定为异常流量。
  Future<HttpClientResponse> sendWithRetry(
    Future<HttpClientRequest> Function(HttpClient client) buildRequest, {
    int maxAttempts = 3,
    CancellationToken? cancel,
    void Function(int attempt, int status)? onRetry,
  }) async {
    Object? lastError;

    for (var attempt = 0; attempt < maxAttempts; attempt++) {
      cancel?.throwIfCancelled();
      await throttle();

      HttpClientResponse? response;
      try {
        final request = await buildRequest(_http);
        response = await request.close();

        if (_isRetryableStatus(response.statusCode) && attempt < maxAttempts - 1) {
          onRetry?.call(attempt, response.statusCode);
          // 必须把响应体读完/丢弃，否则连接不会归还连接池
          await response.drain<void>();
          await _backoff(attempt);
          continue;
        }

        // 关键：每次响应都吸收 Set-Cookie，保证登录态/会话 Cookie 不丢
        jar.absorbResponse(response);
        return response;
      } catch (e) {
        lastError = e;
        if (attempt < maxAttempts - 1) {
          await _backoff(attempt);
          continue;
        }
      }
    }

    if (lastError is CancelledException) throw lastError;
    throw QuarkApiException('网络请求失败（已重试 $maxAttempts 次）: $lastError');
  }

  /// 指数退避 + 随机抖动：约 0.6s, 1.4s, 3.0s（±40%），上限 15s。
  Future<void> _backoff(int attempt) async {
    final baseMs = 600 * pow(2.2, attempt);
    final jitter = 0.6 + 0.8 * _rng.nextDouble();
    final ms = min((baseMs * jitter).round(), 15000);
    await Future<void>.delayed(Duration(milliseconds: ms));
  }

  // ---------------- JSON 便捷方法 ----------------

  /// GET 并解析 JSON。
  Future<dynamic> getJson(
    String url, {
    int timeoutSeconds = 30,
    CancellationToken? cancel,
  }) async {
    final response = await sendWithRetry(
      (client) async {
        final request = await client.getUrl(Uri.parse(url));
        buildHeaders().forEach(request.headers.set);
        return request;
      },
      cancel: cancel,
    );
    return _decodeJson(response);
  }

  /// POST JSON 并解析响应。
  Future<dynamic> postJson(
    String url,
    Object body, {
    int timeoutSeconds = 30,
    CancellationToken? cancel,
  }) async {
    final response = await sendWithRetry(
      (client) async {
        final request = await client.postUrl(Uri.parse(url));
        buildHeaders(json: true).forEach(request.headers.set);
        request.add(utf8.encode(jsonEncode(body)));
        return request;
      },
      cancel: cancel,
    );
    return _decodeJson(response);
  }

  Future<dynamic> _decodeJson(HttpClientResponse response) async {
    final text = await response.transform(utf8.decoder).join();

    if (response.statusCode >= 400) {
      throw QuarkApiException(
        _authHint(response.statusCode) ??
            'HTTP ${response.statusCode}: ${_snippet(text)}',
        httpStatus: response.statusCode,
      );
    }

    try {
      return jsonDecode(text);
    } catch (_) {
      // 接口返回了非 JSON（通常是风控页或登录页）—— 原样带回片段便于排查
      throw QuarkApiException(
        '响应不是合法 JSON: ${_snippet(text)}',
        httpStatus: response.statusCode,
      );
    }
  }

  static String _snippet(String text) {
    final oneLine = text.replaceAll(RegExp(r'\s+'), ' ').trim();
    return oneLine.length <= 160 ? oneLine : '${oneLine.substring(0, 160)}…';
  }

  /// 把鉴权类状态码翻译成用户看得懂的提示。
  ///
  /// 不翻译的话用户只会看到「请求失败」，完全不知道是登录过期还是被风控。
  static String? _authHint(int code) {
    switch (code) {
      case 401:
        return '登录态已失效（401），请重新登录';
      case 403:
        return '访问被拒绝（403），可能是登录态失效或权限不足';
      case 412:
        return '请求被拦截（412）。若持续出现，请检查登录态或稍后重试';
      case 429:
        return '请求过于频繁（429），已自动退避，请稍后再试';
      default:
        return null;
    }
  }

  void close() => _http.close(force: true);
}
