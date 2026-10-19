/// 直链质量评估。
///
/// 移植自桌面版 `GeZi.Core/Support/LinkQuality.cs`。
///
/// 同一个文件从不同官方入口（PC / 网页 / 移动）取回的直链，
/// CDN 节点、签名有效期、限速策略都可能不同。质量差的表现为：
/// 下载中途容易 403/断流、单连接速度低。
///
/// 这里用一个**纯本地的启发式打分**排序候选：分数越高越优先。
/// 判据全部来自 URL 本身 —— 不发额外请求、不触碰任何风控边界。
class LinkQuality {
  LinkQuality._();

  /// 对一条直链打分。返回负值表示「不可用」。
  static int score(String url) {
    if (url.isEmpty || url.length <= 20) return -1;

    var score = 0;

    // 1) 必须是 https：http 直链在多数网络下会被劫持或降速
    if (url.startsWith('https://')) score += 100;

    // 2) 签名参数完整度：官方直链通常带这些，缺了就容易过期
    if (url.contains('auth_key=')) score += 30;
    if (url.contains('Expires=')) score += 15;
    if (url.contains('Signature=')) score += 15;

    // 3) 域名倾向：直连 CDN 域名通常比走跳转域名少一跳
    if (url.contains('.quark.cn')) score += 10;
    if (url.contains('.uc.cn')) score += 6;
    if (url.contains('cdn')) score += 5;

    // 4) 明显是占位/错误页的，直接判负
    final lower = url.toLowerCase();
    if (lower.contains('error') ||
        lower.contains('denied') ||
        lower.contains('forbidden')) {
      score -= 200;
    }

    // 5) 极短 URL 多半是跳转壳而非真实直链
    if (url.length < 60) score -= 20;

    return score;
  }

  /// 是否可用（分数非负）。
  static bool isUsable(String url) => score(url) >= 0;

  /// 从一组候选中挑出总分最高的一组。
  static List<T>? pickBest<T>(
    List<List<T>> candidates,
    String Function(T) urlOf,
  ) {
    List<T>? best;
    var bestScore = -1;
    for (final candidate in candidates) {
      if (candidate.isEmpty) continue;
      final total = candidate.fold<int>(
        0,
        (sum, item) {
          final s = score(urlOf(item));
          return sum + (s < 0 ? 0 : s);
        },
      );
      if (total > bestScore) {
        bestScore = total;
        best = candidate;
      }
    }
    return best;
  }
}
