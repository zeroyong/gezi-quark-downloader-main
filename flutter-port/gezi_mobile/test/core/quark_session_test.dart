import 'package:flutter_test/flutter_test.dart';
import 'package:gezi_mobile/core/net/cookie_jar.dart';
import 'package:gezi_mobile/core/net/quark_session.dart';

void main() {
  group('CookieJar 解析', () {
    test('解析简单 Cookie', () {
      final jar = CookieJar()..seedFromString('a=1; b=2');
      expect(jar['a'], '1');
      expect(jar['b'], '2');
      expect(jar.length, 2);
    });

    test('解析带空格的 Cookie', () {
      final jar = CookieJar()..seedFromString('  a = 1 ;  b = 2  ');
      expect(jar['a'], '1');
      expect(jar['b'], '2');
    });

    test('忽略无等号的片段', () {
      final jar = CookieJar()..seedFromString('a=1; garbage; b=2');
      expect(jar.length, 2);
      expect(jar['a'], '1');
    });

    test('值里含等号时只按第一个等号切分', () {
      final jar = CookieJar()..seedFromString('token=abc=def==');
      expect(jar['token'], 'abc=def==');
    });

    test('吸收 Set-Cookie 行（带属性）', () {
      final jar = CookieJar()
        ..absorbSetCookieLine('__puus=xyz; Path=/; Domain=.quark.cn; HttpOnly');
      expect(jar['__puus'], 'xyz');
    });

    test('吸收多行 Set-Cookie', () {
      final jar = CookieJar()
        ..absorbSetCookieLine('a=1; Path=/')
        ..absorbSetCookieLine('b=2; Path=/');
      expect(jar.toHeaderValue(), 'a=1; b=2');
    });

    test('后到的同名 Cookie 覆盖先到的', () {
      final jar = CookieJar()..seedFromString('a=old');
      jar.absorbSetCookieLine('a=new; Path=/');
      expect(jar['a'], 'new');
    });

    test('toHeaderValue 空罐返回空串', () {
      expect(CookieJar().toHeaderValue(), '');
    });

    test('hasLogin 识别 __puus', () {
      expect((CookieJar()..seedFromString('__puus=x')).hasLogin, isTrue);
    });

    test('hasLogin 识别 __pus', () {
      expect((CookieJar()..seedFromString('__pus=x')).hasLogin, isTrue);
    });

    test('hasLogin 识别 _UP_ 前缀', () {
      expect((CookieJar()..seedFromString('_UP_A=x; _UP_B=y')).hasLogin, isTrue);
    });

    test('无登录 Cookie 时 hasLogin 为 false', () {
      expect((CookieJar()..seedFromString('a=1; b=2')).hasLogin, isFalse);
    });
  });

  group('CancellationToken', () {
    test('初始未取消', () {
      expect(CancellationToken().isCancelled, isFalse);
    });

    test('取消后 throwIfCancelled 抛异常', () {
      final token = CancellationToken()..cancel();
      expect(token.isCancelled, isTrue);
      expect(() => token.throwIfCancelled(), throwsA(isA<CancelledException>()));
    });

    test('未取消时 throwIfCancelled 不抛', () {
      expect(() => CancellationToken().throwIfCancelled(), returnsNormally);
    });

    test('注册的回调会被触发', () {
      var called = 0;
      final token = CancellationToken()..onCancel(() => called++);
      token.cancel();
      expect(called, 1);
    });

    test('已取消后注册回调会立即执行', () {
      final token = CancellationToken()..cancel();
      var called = 0;
      token.onCancel(() => called++);
      expect(called, 1, reason: '避免竞态下漏掉清理逻辑');
    });

    test('重复取消只触发一次', () {
      var called = 0;
      final token = CancellationToken()..onCancel(() => called++);
      token.cancel();
      token.cancel();
      expect(called, 1);
    });

    test('回调抛异常不影响其他回调', () {
      var second = 0;
      final token = CancellationToken()
        ..onCancel(() => throw StateError('boom'))
        ..onCancel(() => second++);
      token.cancel();
      expect(second, 1);
    });
  });

  group('PauseToken', () {
    test('初始未暂停', () {
      expect(PauseToken().isPaused, isFalse);
    });

    test('暂停后 waitIfPaused 挂起', () async {
      final pause = PauseToken()..pause();
      final cancel = CancellationToken();
      var resumed = false;

      final future = pause.waitIfPaused(cancel).then((_) => resumed = true);
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(resumed, isFalse, reason: '暂停期间不应继续');

      pause.resume();
      await future;
      expect(resumed, isTrue);
    });

    test('未暂停时 waitIfPaused 立即返回', () async {
      await PauseToken().waitIfPaused(CancellationToken());
      expect(true, isTrue);
    });

    test('暂停期间取消应唤醒并抛 CancelledException', () async {
      final pause = PauseToken()..pause();
      final cancel = CancellationToken();

      final future = pause.waitIfPaused(cancel);
      await Future<void>.delayed(const Duration(milliseconds: 30));
      cancel.cancel();

      await expectLater(future, throwsA(isA<CancelledException>()));
    });
  });

  group('QuarkSession 请求头', () {
    test('包含完整浏览器头（缺项本身就是可疑特征）', () {
      final session = QuarkSession();
      final headers = session.buildHeaders();

      expect(headers['User-Agent'], contains('quark-cloud-drive'));
      expect(headers['Referer'], 'https://pan.quark.cn/');
      expect(headers['Origin'], 'https://pan.quark.cn');
      expect(headers['Accept-Language'], contains('zh-CN'));
      expect(headers['Sec-Fetch-Dest'], isNotNull);
      expect(headers['Sec-Fetch-Mode'], isNotNull);
      expect(headers['Sec-Fetch-Site'], isNotNull);
      session.close();
    });

    test('API 与下载使用不同 Accept 但相同 UA', () {
      final session = QuarkSession();
      final api = session.buildHeaders(api: true);
      final dl = session.buildHeaders(api: false);

      expect(api['Accept'], contains('application/json'));
      expect(dl['Accept'], '*/*');
      expect(api['User-Agent'], dl['User-Agent'],
          reason: '取链与下载必须同族 UA，否则 CDN 会判定盗链');
      session.close();
    });

    test('Cookie 会进入请求头', () {
      final session = QuarkSession(cookie: '__puus=abc; __pugs=def');
      final headers = session.buildHeaders();
      expect(headers['Cookie'], contains('__puus=abc'));
      expect(headers['Cookie'], contains('__pugs=def'));
      session.close();
    });

    test('空 Cookie 罐时不设 Cookie 头', () {
      final session = QuarkSession();
      expect(session.buildHeaders().containsKey('Cookie'), isFalse);
      session.close();
    });

    test('json 模式带 Content-Type', () {
      final session = QuarkSession();
      expect(session.buildHeaders(json: true)['Content-Type'], 'application/json');
      session.close();
    });
  });

  group('QuarkSession 真实网络（连通性）', () {
    /// 这个测试会真的联网。它验证的是**最关键的移植前提**：
    /// Dart 的 TLS 栈能访问夸克的域名（桌面版为此内置了 20MB CPython）。
    test(
      '能连上 uop.quark.cn 并拿到扫码 token',
      () async {
        final session = QuarkSession();
        try {
          final res = await session.getJson(
            'https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin'
            '?client_id=532&v=1.2&request_id=${DateTime.now().millisecondsSinceEpoch}',
          );
          expect(res, isA<Map>());
          expect((res as Map)['status'], 2000000);
          final token = res['data']?['members']?['token'];
          expect(token, isA<String>());
          expect((token as String).isNotEmpty, isTrue);
        } finally {
          session.close();
        }
      },
      timeout: const Timeout(Duration(seconds: 40)),
    );

    /// 验证 `pan.quark.cn` 可达 —— 这是桌面版**做不到**的那个域名。
    test(
      '能连上 pan.quark.cn（桌面版被 TLS 指纹过滤的域名）',
      () async {
        final session = QuarkSession();
        try {
          final res = await session.getJson('https://pan.quark.cn/account/info');
          expect(res, isA<Map>());
          // 未登录时返回 {"success":true,"data":{},"code":"OK"}
          expect((res as Map)['success'], isTrue);
        } finally {
          session.close();
        }
      },
      timeout: const Timeout(Duration(seconds: 40)),
    );
  });

  group('QuarkApiException', () {
    test('鉴权类错误识别', () {
      expect(
        const QuarkApiException('x', httpStatus: 412).isAuthError,
        isTrue,
      );
      expect(
        const QuarkApiException('x', httpStatus: 401).isAuthError,
        isTrue,
      );
      expect(
        const QuarkApiException('x', httpStatus: 500).isAuthError,
        isFalse,
      );
    });

    test('toString 带上下文', () {
      const e = QuarkApiException('失败', httpStatus: 412, code: 14001);
      expect(e.toString(), contains('412'));
      expect(e.toString(), contains('14001'));
    });
  });
}
