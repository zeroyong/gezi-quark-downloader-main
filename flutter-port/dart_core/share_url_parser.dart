// GeZi Flutter 移植 —— 分享链接解析
//
// 对应 C# 的 GeZi.Core/Support/ShareUrlParser.cs
// 纯函数，无 IO，最容易直接移植的一层。

/// 分享链接解析结果
class ShareUrlInfo {
  final String pwdId;
  final String passcode;
  final String startFid;
  final String kouling;

  const ShareUrlInfo({
    this.pwdId = '',
    this.passcode = '',
    this.startFid = '0',
    this.kouling = '',
  });

  /// 是纯口令（形如 /~469d3M9zTu~:/）而不是链接。
  /// 夸克没有公开的「口令 → 分享链接」接口，只能识别并提示用户改用链接。
  bool get isKouling => kouling.isNotEmpty && pwdId.isEmpty;
  bool get isValid => pwdId.isNotEmpty;
}

class ShareUrlParser {
  static final _shareId = RegExp(r'/s/([A-Za-z0-9_-]+)');
  static final _passcode = RegExp(r'pwd=([A-Za-z0-9]+)');

  // 中文文案里的提取码。
  // ⚠️ 负向后顾必须有：分享 ID 本身可能就叫 pwd1234
  //    （https://pan.quark.cn/s/pwd1234），不加会把 "pwd1234" 解析成提取码 "1234"。
  static final _passcodeCn = RegExp(
    r'(?<![/A-Za-z0-9])(?:提取码|提取碼|密码|密碼|访问码|訪問碼|口令|pwd|PWD|Pwd)\s*[=:：]?\s*([A-Za-z0-9]{1,12})',
  );

  static final _kouling = RegExp(r'/~([A-Za-z0-9]{4,32})~:?/?');
  static final _fid32 = RegExp(r'/([A-Za-z0-9]{32})(?:-[^/?#]*)?');

  /// 解析分享链接 / 整段分享文案 / 夸克口令。
  static ShareUrlInfo parse(String input) {
    if (input.isEmpty) return const ShareUrlInfo();

    var pwdId = '';
    var passcode = '';
    var startFid = '0';
    var kouling = '';

    final m1 = _shareId.firstMatch(input);
    if (m1 != null) pwdId = m1.group(1)!;

    final m2 = _passcode.firstMatch(input);
    if (m2 != null) passcode = m2.group(1)!;

    if (passcode.isEmpty) {
      final m3 = _passcodeCn.firstMatch(input);
      if (m3 != null) passcode = m3.group(1)!;
    }

    final m4 = _kouling.firstMatch(input);
    if (m4 != null) kouling = m4.group(1)!;

    for (final fm in _fid32.allMatches(input)) {
      final fid = fm.group(1)!;
      if (fid != pwdId) startFid = fid;
    }

    return ShareUrlInfo(
      pwdId: pwdId,
      passcode: passcode,
      startFid: startFid,
      kouling: kouling,
    );
  }
}
