/// 分享链接解析。
///
/// 移植自桌面版 `GeZi.Core/Support/ShareUrlParser.cs`。
/// 纯函数、无 IO、无网络 —— 最容易移植也最容易测试的一层。
class ShareUrlInfo {
  /// 分享 ID（`https://pan.quark.cn/s/<pwdId>` 里的那一段）。
  final String pwdId;

  /// 提取码。无码分享为空串。
  final String passcode;

  /// 分享内定位到的起始目录 fid，默认 '0'（根目录）。
  final String startFid;

  /// 夸克口令里的码（形如 `/~469d3M9zTu~:/`）。
  final String kouling;

  const ShareUrlInfo({
    this.pwdId = '',
    this.passcode = '',
    this.startFid = '0',
    this.kouling = '',
  });

  bool get isValid => pwdId.isNotEmpty;

  /// 是纯口令而不是链接。
  ///
  /// 夸克没有公开的「口令 → 分享链接」接口，所以只能识别出来并提示
  /// 用户改用分享链接，不能真的解析。
  bool get isKouling => kouling.isNotEmpty && pwdId.isEmpty;

  @override
  String toString() =>
      'ShareUrlInfo(pwdId=$pwdId, passcode=$passcode, startFid=$startFid)';
}

class ShareUrlParser {
  ShareUrlParser._();

  /// `/s/xxxxx` —— 分享 ID。
  static final RegExp _shareIdPattern = RegExp(r'/s/([A-Za-z0-9_-]+)');

  /// `?pwd=xxxx` —— 链接参数里的提取码。
  static final RegExp _passcodePattern = RegExp(r'pwd=([A-Za-z0-9]+)');

  /// 中文文案里的提取码：`提取码：abcd` / `密码:abcd` / `访问码 abcd` …
  ///
  /// ⚠️ 前面的负向后顾 `(?<![/A-Za-z0-9])` 是**必须的**：
  /// 分享 ID 本身可能就叫 `pwd1234`（`https://pan.quark.cn/s/pwd1234`），
  /// 不加这个断言会把 "pwd1234" 解析成提取码 "1234"。
  static final RegExp _passcodeCnPattern = RegExp(
    r'(?<![/A-Za-z0-9])(?:提取码|提取碼|密码|密碼|访问码|訪問碼|口令|pwd|PWD|Pwd)'
    r'\s*[=:：]?\s*([A-Za-z0-9]{1,12})',
  );

  /// 夸克客户端口令：`/~469d3M9zTu~:/`
  static final RegExp _koulingPattern = RegExp(r'/~([A-Za-z0-9]{4,32})~:?/?');

  /// 32 位 fid（用于分享链接里直接定位子目录）。
  static final RegExp _fid32Pattern =
      RegExp(r'/([A-Za-z0-9]{32})(?:-[^/?#]*)?');

  /// 解析分享链接、整段分享文案，或识别夸克口令。
  ///
  /// 输入可以是：
  ///   · `https://pan.quark.cn/s/abc123`
  ///   · `https://pan.quark.cn/s/abc123?pwd=x9k2`
  ///   · `链接：https://pan.quark.cn/s/abc123 提取码：x9k2`
  ///   · `/~469d3M9zTu~:/`（纯口令）
  static ShareUrlInfo parse(String input) {
    if (input.isEmpty) return const ShareUrlInfo();

    var pwdId = '';
    var passcode = '';
    var startFid = '0';
    var kouling = '';

    final shareMatch = _shareIdPattern.firstMatch(input);
    if (shareMatch != null) {
      pwdId = shareMatch.group(1) ?? '';
    }

    // 优先取链接参数里的 pwd=，其次才从中文文案里找
    final pwdMatch = _passcodePattern.firstMatch(input);
    if (pwdMatch != null) {
      passcode = pwdMatch.group(1) ?? '';
    }
    if (passcode.isEmpty) {
      final cnMatch = _passcodeCnPattern.firstMatch(input);
      if (cnMatch != null) {
        passcode = cnMatch.group(1) ?? '';
      }
    }

    final koulingMatch = _koulingPattern.firstMatch(input);
    if (koulingMatch != null) {
      kouling = koulingMatch.group(1) ?? '';
    }

    // 32 位 fid 若与 pwdId 相同则说明那只是分享 ID，不是子目录
    for (final m in _fid32Pattern.allMatches(input)) {
      final fid = m.group(1) ?? '';
      if (fid.isNotEmpty && fid != pwdId) {
        startFid = fid;
      }
    }

    return ShareUrlInfo(
      pwdId: pwdId,
      passcode: passcode,
      startFid: startFid,
      kouling: kouling,
    );
  }
}
