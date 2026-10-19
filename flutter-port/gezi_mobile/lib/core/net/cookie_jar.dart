import 'dart:async';
import 'dart:io';

/// 手写 Cookie 罐。
///
/// ============================================================================
/// 【为什么必须自己实现】
///
/// Dart 的 `HttpClient` **不会自动管理 Cookie** —— 没有 .NET `CookieContainer`
/// 的对应物。`Set-Cookie` 响应头必须手动解析并保存，后续请求手动附加。
///
/// 【不实现会怎样（实测结论）】
///
/// 夸克 CDN 的直链绑定在「取链时的那个 Cookie 会话」上。实测对照：
///   · 新鲜直链 + 不带 Cookie + Range → HTTP 412
///   · 新鲜直链 + 带 Cookie  + Range → HTTP 206 ✅
///   · 换新会话取链 + 补旧 Cookie     → HTTP 412（Cookie 必须同源同会话）
///
/// 用 PowerShell / curl 请求同一 URL 也是 412，证明这是服务端强校验，
/// 不是 Dart 特有的问题。`__sdid` / `__pugs` 这两个 Cookie 由
/// `sharepage/token` 和 `file/download` 的 Set-Cookie 下发，缺了必被拒。
///
/// 所以：**从第一次 API 请求到最后的字节下载，必须是同一个 Cookie 罐。**
/// ============================================================================
class CookieJar {
  final Map<String, String> _map = <String, String>{};

  /// 解析 `HttpClientResponse` 的 Set-Cookie 并合并。
  ///
  /// 一条 Set-Cookie 的格式是 `name=value; Path=/; Domain=.quark.cn; HttpOnly`，
  /// 我们只关心 name=value —— 夸克的 Cookie 全落在 .quark.cn 域，
  /// 不需要按 path/domain 做精细匹配（那套机制在这里是纯负担）。
  void absorbResponse(HttpClientResponse response) {
    final setCookies = response.headers['set-cookie'];
    if (setCookies == null || setCookies.isEmpty) return;
    for (final line in setCookies) {
      absorbSetCookieLine(line);
    }
  }

  /// 吸收一行 Set-Cookie。独立出来便于单元测试。
  void absorbSetCookieLine(String line) {
    if (line.isEmpty) return;
    final first = line.split(';').first;
    final eq = first.indexOf('=');
    if (eq <= 0) return;
    final name = first.substring(0, eq).trim();
    final value = first.substring(eq + 1).trim();
    if (name.isEmpty) return;
    _map[name] = value;
  }

  /// 从一整串 Cookie（`a=1; b=2`）导入，用于持久化恢复与用户手动导入。
  void seedFromString(String cookieString) {
    for (final part in cookieString.split(';')) {
      final p = part.trim();
      if (p.isEmpty) continue;
      final eq = p.indexOf('=');
      if (eq <= 0) continue;
      final name = p.substring(0, eq).trim();
      if (name.isEmpty) continue;
      _map[name] = p.substring(eq + 1).trim();
    }
  }

  String? operator [](String name) => _map[name];

  bool has(String name) => _map.containsKey(name);

  /// 是否有登录态。兼容 `__puus` 与 `_UP_*` 两种表示。
  bool get hasLogin =>
      has('__puus') || has('__pus') || _map.keys.any((k) => k.startsWith('_UP_'));

  Iterable<String> get keys => _map.keys;

  int get length => _map.length;

  /// 拼成请求头用的 Cookie 串。空罐返回空串（调用方应据此不设 Cookie 头）。
  String toHeaderValue() =>
      _map.entries.map((e) => '${e.key}=${e.value}').join('; ');

  /// 快照，用于持久化。
  Map<String, String> toMap() => Map<String, String>.unmodifiable(_map);

  void clear() => _map.clear();

  @override
  String toString() => 'CookieJar(${_map.length} keys: ${_map.keys.join(", ")})';
}

/// 取消令牌。
///
/// Dart 没有内建取消机制（`Future` 不可取消），而下载器到处需要
/// 「用户取消 → 立刻停止所有分片」。统一用这个，避免每处自己写 bool 标志
/// 导致漏判。
class CancellationToken {
  bool _cancelled = false;
  final List<void Function()> _listeners = <void Function()>[];

  bool get isCancelled => _cancelled;

  void cancel() {
    if (_cancelled) return;
    _cancelled = true;
    // 复制一份再遍历：监听器内部可能再次注册/取消，直接遍历会并发修改
    final snapshot = List<void Function()>.of(_listeners);
    _listeners.clear();
    for (final listener in snapshot) {
      try {
        listener();
      } catch (_) {
        // 单个监听器异常不能影响其他监听器
      }
    }
  }

  void throwIfCancelled() {
    if (_cancelled) throw const CancelledException();
  }

  /// 注册取消回调。若已取消，立刻同步执行（避免竞态下漏掉清理）。
  void onCancel(void Function() callback) {
    if (_cancelled) {
      callback();
    } else {
      _listeners.add(callback);
    }
  }
}

/// 取消异常。用独立的类型而不是通用 Exception，
/// 便于上层 `catch` 时精确区分「用户取消」与「真的出错了」。
class CancelledException implements Exception {
  const CancelledException();
  @override
  String toString() => 'CancelledException';
}

/// 暂停令牌。
///
/// 与取消的区别：暂停是可恢复的，且**不能**中断正在进行的网络读 ——
/// 分片 worker 在每次读循环里检查暂停状态，挂起等待而不是抛异常。
class PauseToken {
  bool _paused = false;
  final List<Completer<void>> _resumeWaiters = <Completer<void>>[];

  bool get isPaused => _paused;

  void pause() => _paused = true;

  void resume() {
    _paused = false;
    final snapshot = List<Completer<void>>.of(_resumeWaiters);
    _resumeWaiters.clear();
    for (final c in snapshot) {
      if (!c.isCompleted) c.complete();
    }
  }

  /// 若处于暂停状态则挂起，直到 resume() 或取消。
  Future<void> waitIfPaused(CancellationToken cancel) async {
    if (!_paused) return;
    final completer = Completer<void>();
    _resumeWaiters.add(completer);
    // 取消时也要唤醒，否则取消后 worker 会永远卡在暂停里
    cancel.onCancel(() {
      if (!completer.isCompleted) completer.complete();
    });
    await completer.future;
    cancel.throwIfCancelled();
  }
}

/// 夸克接口异常。
class QuarkApiException implements Exception {
  final String message;
  final int? httpStatus;
  final int? code;

  const QuarkApiException(this.message, {this.httpStatus, this.code});

  /// 是否为鉴权类错误（登录失效 / 被风控拦截）。
  ///
  /// 这类错误值得做一次「刷新直链后重试」，而不是直接判任务失败。
  bool get isAuthError =>
      httpStatus == 401 || httpStatus == 403 || httpStatus == 412;

  @override
  String toString() {
    final parts = <String>[];
    if (httpStatus != null) parts.add('HTTP $httpStatus');
    if (code != null) parts.add('code $code');
    final suffix = parts.isEmpty ? '' : ' [${parts.join(", ")}]';
    return '$message$suffix';
  }
}
