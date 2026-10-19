import 'package:flutter_test/flutter_test.dart';
import 'package:gezi_mobile/core/download/thread_allocator.dart';
import 'package:gezi_mobile/core/util/link_quality.dart';

void main() {
  group('ThreadAllocator 连接分配', () {
    test('空列表返回空', () {
      expect(ThreadAllocator.allocate([]), isEmpty);
    });

    test('单文件直接给上限', () {
      final r = ThreadAllocator.allocate([100 * 1024 * 1024], perFileCap: 16);
      expect(r, [16]);
    });

    test('大文件分到更多连接', () {
      final r = ThreadAllocator.allocate(
        [
          10 * 1024 * 1024 * 1024, // 10GB
          500 * 1024 * 1024, // 500MB
          10 * 1024 * 1024, // 10MB
        ],
        perFileCap: 16,
      );
      expect(r[0], greaterThan(r[1]));
      expect(r[1], greaterThanOrEqualTo(r[2]));
      expect(r[0], lessThanOrEqualTo(16), reason: '不得超过单文件上限');
    });

    test('小文件有保底下限', () {
      final r = ThreadAllocator.allocate(
        [
          100 * 1024 * 1024 * 1024, // 100GB
          1024, // 1KB
        ],
        perFileCap: 16,
        minPerFile: 2,
      );
      expect(r[1], greaterThanOrEqualTo(2), reason: '小文件也要下得动');
    });

    test('大小未知时退回平分', () {
      final r = ThreadAllocator.allocate([0, 0, 0], perFileCap: 16);
      expect(r.every((x) => x == r[0]), isTrue);
      expect(r[0], greaterThanOrEqualTo(2));
    });

    test('全部结果落在 [下限, 上限] 内', () {
      final sizes = List<int>.generate(20, (i) => (i + 1) * 1024 * 1024);
      final r = ThreadAllocator.allocate(sizes, perFileCap: 16, minPerFile: 2);
      for (final v in r) {
        expect(v, greaterThanOrEqualTo(2));
        expect(v, lessThanOrEqualTo(16));
      }
    });

    test('等大文件退化为平分', () {
      final r = ThreadAllocator.allocate(
        List<int>.filled(4, 100 * 1024 * 1024),
        perFileCap: 16,
      );
      expect(r.toSet().length, 1, reason: '等大文件应分到相同连接数');
    });
  });

  group('DownloadTuning 分片规划', () {
    test('小文件不分片', () {
      expect(DownloadTuning.planSegmentCount(1024, 16), 1);
      expect(DownloadTuning.planSegmentCount(4 * 1024 * 1024, 16), 1);
    });

    test('大文件分片数 = max(连接数×8, 大小/8MB)', () {
      // 1GB / 8MB = 128 片；连接数 16 × 8 = 128 片 → 取 128
      final count = DownloadTuning.planSegmentCount(1024 * 1024 * 1024, 16);
      expect(count, greaterThanOrEqualTo(128));
    });

    test('分片数不超过 4096', () {
      final count =
          DownloadTuning.planSegmentCount(1024 * 1024 * 1024 * 1024, 64);
      expect(count, lessThanOrEqualTo(4096));
    });

    test('分片数总是 ≥ 1', () {
      expect(DownloadTuning.planSegmentCount(0, 16), greaterThanOrEqualTo(1));
      expect(DownloadTuning.planSegmentCount(-1, 16), greaterThanOrEqualTo(1));
    });

    test('片数应大于连接数（工作窃取的前提）', () {
      final count = DownloadTuning.planSegmentCount(100 * 1024 * 1024, 16);
      expect(count, greaterThan(16),
          reason: '片数必须多于连接数，否则慢连接会拖住整体');
    });
  });

  group('LinkQuality 直链打分', () {
    test('空 URL 不可用', () {
      expect(LinkQuality.score(''), lessThan(0));
      expect(LinkQuality.score('https://a.cn'), lessThan(0));
    });

    test('https + 完整签名得分高', () {
      const good = 'https://dl-guest-zb-u.drive.quark.cn/abc/def'
          '?auth_key=123-456&Expires=999&Signature=xyz&token=abcdefghijklmnop';
      expect(LinkQuality.score(good), greaterThan(100));
      expect(LinkQuality.isUsable(good), isTrue);
    });

    test('http 比 https 分低', () {
      const https = 'https://dl.quark.cn/a/auth_key=1&Expires=2&Signature=3';
      const http = 'http://dl.quark.cn/a/auth_key=1&Expires=2&Signature=3';
      expect(LinkQuality.score(https), greaterThan(LinkQuality.score(http)));
    });

    test('错误页 URL 直接判负', () {
      const bad = 'https://dl.quark.cn/error/forbidden/denied?auth_key=123456';
      expect(LinkQuality.score(bad), lessThan(0));
      expect(LinkQuality.isUsable(bad), isFalse);
    });

    test('pickBest 选出总分最高的一组', () {
      final candidates = <List<String>>[
        ['https://short.cn/x'], // 低分
        [
          'https://dl.quark.cn/a?auth_key=1&Expires=2&Signature=3&token=abcdef',
          'https://dl.quark.cn/b?auth_key=1&Expires=2&Signature=3&token=abcdef',
        ], // 高分
      ];
      final best = LinkQuality.pickBest<String>(candidates, (s) => s);
      expect(best, isNotNull);
      expect(best!.length, 2);
    });

    test('pickBest 全空返回 null', () {
      expect(LinkQuality.pickBest<String>([[], []], (s) => s), isNull);
    });
  });
}
