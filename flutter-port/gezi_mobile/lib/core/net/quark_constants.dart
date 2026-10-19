/// 夸克接口常量。
///
/// 移植自桌面版 `GeZi.Core/Support/QuarkConstants.cs`。
/// 所有取值均来自桌面版已验证的实现，不要随意改动。
class QuarkConstants {
  const QuarkConstants._();

  static const String apiBase = 'https://drive-pc.quark.cn';
  static const String accountUrl = 'https://pan.quark.cn/account/info';
  static const String referer = 'https://pan.quark.cn/';
  static const String origin = 'https://pan.quark.cn';

  /// API 调用用的 UA：伪装成夸克网盘 PC 客户端（Electron）。
  ///
  /// **取链与下载必须使用同一个 UA**。夸克 CDN 会对「取链时声明的客户端身份」
  /// 与「实际下载时的身份」做一致性校验，换 UA 等于自证非客户端，
  /// 更容易被判定为盗链而降速或返回 412。
  static const String userAgent =
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 '
      '(KHTML, like Gecko) quark-cloud-drive/3.14.2 Chrome/112.0.5615.165 '
      'Electron/24.1.3.8 Safari/537.36 Channel/pckk_other_ch';

  /// 登录态关键 Cookie。缺它就没登录上。
  static const String loginCookieKey = '__puus';

  /// 登录态的备用关键 Cookie（前缀匹配）。
  ///
  /// `pan.quark.cn` 与 `uop.quark.cn` 上的登录态是两组等价表示，
  /// 校验登录态时不能只认 `__puus`，否则会把一份可用的 Cookie 判成无效。
  static const String loginCookiePrefix = '_UP_';

  /// 公共查询串。绝大多数接口都要带。
  static const String commonQuery = 'pr=ucpro&fr=pc&uc_param_str=';

  // ---------------- 扫码登录（二期 WebView 方案会用到）----------------
  static const String qrGetToken =
      'https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin';
  static const String qrGetTicket =
      'https://uop.quark.cn/cas/ajax/getServiceTicketByQrcodeToken';

  static const int qrStatusOk = 2000000;
  static const int qrWaitingScan = 50004001;
  static const int qrTokenNotFound = 50004002;
  static const int qrTokenExpired = 50004003;

  // ---------------- 下载参数（移动端已下调）----------------

  /// 单次读取缓冲。
  ///
  /// 桌面版是 256KB。移动端降到 64KB：内存更省，且在小分片上
  /// 首字节延迟更低。实测对吞吐没有可观测影响（瓶颈在 CDN 单连接限速）。
  static const int readChunk = 64 * 1024;

  /// 小于此值不分片。
  ///
  /// 与桌面版一致（4MB）。太小的文件分片反而是负收益：
  /// 多连接握手开销 > 并行收益。
  static const int minSegmentSize = 4 * 1024 * 1024;

  /// 分片数量上限。防止超大文件产生海量小片。
  static const int maxSegments = 4096;
}
