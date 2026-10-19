import 'package:flutter_test/flutter_test.dart';
import 'package:gezi_mobile/core/util/share_url_parser.dart';

void main() {
  group('ShareUrlParser 分享 ID 解析', () {
    test('标准分享链接', () {
      final info = ShareUrlParser.parse('https://pan.quark.cn/s/a83dbadbc586');
      expect(info.pwdId, 'a83dbadbc586');
      expect(info.passcode, '');
      expect(info.isValid, isTrue);
    });

    test('带 pwd 参数的链接', () {
      final info = ShareUrlParser.parse('https://pan.quark.cn/s/abc123?pwd=x9k2');
      expect(info.pwdId, 'abc123');
      expect(info.passcode, 'x9k2');
    });

    test('带 - 和 _ 的分享 ID', () {
      final info = ShareUrlParser.parse('https://pan.quark.cn/s/a-b_c123');
      expect(info.pwdId, 'a-b_c123');
    });

    test('空输入返回无效结果', () {
      expect(ShareUrlParser.parse('').isValid, isFalse);
      expect(ShareUrlParser.parse('随便一段文字').isValid, isFalse);
    });

    test('非夸克链接不误判', () {
      final info = ShareUrlParser.parse('https://example.com/foo/bar');
      expect(info.isValid, isFalse);
    });
  });

  group('ShareUrlParser 提取码', () {
    test('中文文案 提取码：', () {
      final info = ShareUrlParser.parse(
        '链接：https://pan.quark.cn/s/abc123def 提取码：x9k2',
      );
      expect(info.pwdId, 'abc123def');
      expect(info.passcode, 'x9k2');
    });

    test('中文文案 密码:', () {
      final info = ShareUrlParser.parse(
        'https://pan.quark.cn/s/abc123 密码:ab12',
      );
      expect(info.passcode, 'ab12');
    });

    test('访问码', () {
      final info = ShareUrlParser.parse(
        'https://pan.quark.cn/s/abc123 访问码：zz99',
      );
      expect(info.passcode, 'zz99');
    });

    /// 【关键边界用例】分享 ID 本身叫 pwd1234 时，
    /// 不能把 "1234" 误解析成提取码。
    /// 这是负向后顾 `(?<![/A-Za-z0-9])` 存在的唯一理由。
    test('分享 ID 叫 pwd1234 时不应误判提取码', () {
      final info = ShareUrlParser.parse('https://pan.quark.cn/s/pwd1234');
      expect(info.pwdId, 'pwd1234');
      expect(info.passcode, '', reason: '负向后顾失效：把分享 ID 当成了提取码');
    });

    test('链接参数 pwd 优先于中文文案', () {
      final info = ShareUrlParser.parse(
        'https://pan.quark.cn/s/abc123?pwd=real 提取码：fake',
      );
      expect(info.passcode, 'real');
    });
  });

  group('ShareUrlParser 口令识别', () {
    test('识别夸克口令', () {
      final info = ShareUrlParser.parse('/~469d3M9zTu~:/');
      expect(info.kouling, '469d3M9zTu');
      expect(info.pwdId, '');
      expect(info.isKouling, isTrue);
      expect(info.isValid, isFalse);
    });

    test('口令与链接同时存在时以链接为准', () {
      final info = ShareUrlParser.parse(
        '复制这段内容 /~469d3M9zTu~:/ 打开 https://pan.quark.cn/s/abc123',
      );
      expect(info.pwdId, 'abc123');
      expect(info.kouling, '469d3M9zTu');
      expect(info.isKouling, isFalse, reason: '有链接时不应判定为纯口令');
    });
  });

  group('ShareUrlParser 子目录 fid', () {
    test('32 位 fid 作为起始目录', () {
      const fid = '1382ebc0375a4f61af7257e876f7a078';
      final info = ShareUrlParser.parse('https://pan.quark.cn/s/abc123/$fid');
      expect(info.pwdId, 'abc123');
      expect(info.startFid, fid);
    });

    test('无 fid 时默认根目录 0', () {
      final info = ShareUrlParser.parse('https://pan.quark.cn/s/abc123');
      expect(info.startFid, '0');
    });

    test('pwdId 恰好 32 位时不应被当成 fid', () {
      const id32 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
      final info = ShareUrlParser.parse('https://pan.quark.cn/s/$id32');
      expect(info.pwdId, id32);
      expect(info.startFid, '0', reason: '分享 ID 本身不能被当作子目录 fid');
    });
  });
}
