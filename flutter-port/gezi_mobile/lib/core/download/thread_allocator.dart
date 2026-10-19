import 'dart:math';

/// 连接数按文件大小比例分配。
///
/// 移植自桌面版 `GeZi.Core/Download/ThreadAllocator.cs`，
/// 但**预算参数整体下调** —— 桌面版 512 的预算在移动端会直接把手机烫穿。
///
/// 规则：
///   `threads_i = clamp(预算 × size_i / Σsize, 下限, 用户设的单文件并发数)`
///
/// · 只有一个文件 → 直接给上限（行为与"不限"一致）
/// · 多个等大文件 → 退化成平分
/// · 大小全未知   → 也退回平分，不会把所有任务饿到只剩下限
/// · 结果永远落在 [minPerFile, perFileCap]
class ThreadAllocator {
  ThreadAllocator._();

  /// 总连接预算（移动端）。
  ///
  /// 桌面版是 512。移动端定 32 的依据：
  ///   · 蜂窝网络 NAT 会话表远小于家用路由
  ///   · 夸克按账号限总连接，超限会降速甚至拒绝
  ///   · 实测单连接速 40~200KB/s，32 路已足够跑满 4G/5G
  ///   · 再多只增加功耗与发热，吞吐不再提升
  static const int defaultBudget = 32;

  /// 单个文件至少给几条连接 —— 保证小文件也下得动，不被大文件挤成 0。
  ///
  /// 桌面版是 4，移动端降到 2：预算本来就小，下限占太多会削弱
  /// 「大文件优先」的效果。
  static const int minPerFile = 2;

  /// 按大小比例分配连接数，返回与入参等长的数组。
  ///
  /// [sizes] 每个任务的文件字节数（未知填 0 或负数）。
  /// [perFileCap] 用户设的「单文件并发连接数」，同时是每项上限。
  /// [concurrency] 同时最多跑几个任务。
  ///
  /// 【为什么要按"批数"放大预算】
  /// 调度器限制同时只跑 [concurrency] 个任务，所以 N 个文件是分
  /// ⌈N/concurrency⌉ 批跑完的。若预算只按 32 分给全部 N 个，
  /// 文件一多（比如 100 个）每份就被压到下限 2 路 —— 而同时其实只有
  /// 2 个在跑，白白浪费了额度。所以把预算乘上批数，
  /// 这样**同时**在跑的那几个加起来正好落在预算附近，
  /// 而文件之间的大小比例关系完全不变（放大系数是统一的）。
  static List<int> allocate(
    List<int> sizes, {
    int perFileCap = 16,
    int budget = defaultBudget,
    int minPerFile = minPerFile,
    int concurrency = 2,
  }) {
    final cap = perFileCap < 1 ? 1 : perFileCap;
    var floor = minPerFile < 1 ? 1 : minPerFile;
    if (floor > cap) floor = cap;

    final n = sizes.length;
    final result = List<int>.filled(n, 0);
    if (n == 0) return result;

    // 单文件：直接给上限，行为与改动前一致
    if (n == 1) {
      result[0] = cap;
      return result;
    }

    final k = concurrency < 1 ? 1 : concurrency;
    final waves = (n + k - 1) ~/ k;
    final effectiveBudget = budget * (waves < 1 ? 1 : waves);

    var totalSize = 0;
    for (final s in sizes) {
      if (s > 0) totalSize += s;
    }

    // 大小未知 → 退回平分，避免所有任务只吃下限
    if (totalSize <= 0) {
      var even = effectiveBudget ~/ n;
      if (even < floor) even = floor;
      if (even > cap) even = cap;
      for (var i = 0; i < n; i++) {
        result[i] = even;
      }
      return result;
    }

    for (var i = 0; i < n; i++) {
      final share =
          sizes[i] > 0 ? effectiveBudget * (sizes[i] / totalSize) : 0.0;
      var t = share.round();
      if (t < floor) t = floor; // 下限：小文件也要下得动
      if (t > cap) t = cap; // 上限：不超过用户设的档位
      result[i] = t;
    }

    // 四舍五入会让总和略微越界（例如 32/3 → 11×3 = 33）。
    // 把超出部分从「当前最大」的项上扣回来，保证总量不超有效预算。
    // ⚠️ 扣的时候不能把任何一项压到下限以下 —— **下限优先于预算**：
    //    宁可总量略微超预算，也不能让小文件下不动。
    var sum = result.fold<int>(0, (a, b) => a + b);
    var guard = 0;
    while (sum > effectiveBudget && guard++ < 100000) {
      var best = -1;
      for (var i = 0; i < n; i++) {
        if (result[i] > floor && (best < 0 || result[i] > result[best])) {
          best = i;
        }
      }
      if (best < 0) break; // 所有项都到下限了，无法再扣
      result[best]--;
      sum--;
    }

    return result;
  }
}

/// 下载调优参数。
class DownloadTuning {
  DownloadTuning._();

  /// 默认单文件并发连接数。
  ///
  /// 16 是中端机 + 4G/5G 的甜点值。不要盲目调高：
  /// 桌面版 512 的设定在手机上会导致明显发热和耗电，而吞吐并无提升。
  static const int defaultConnections = 16;

  /// 单文件并发上限。超过这个数在手机上只有坏处。
  static const int maxConnections = 64;

  /// 同时下载任务数。
  ///
  /// 桌面版默认 3。移动端建议 1~2：屏幕小、用户注意力集中，
  /// 且并发任务会互相抢带宽，实际总吞吐反而可能下降。
  static const int defaultConcurrentTasks = 2;

  /// 并发任务数上限。
  static const int maxConcurrentTasks = 4;

  /// 分片数量策略：片数 = max(连接数 × 倍数, 文件大小 / 单片目标大小)。
  ///
  /// 让「片数 > 连接数」，快的 worker 可以领下一片，
  /// 形成工作窃取，避免某个慢连接拖住整体。
  static const int segmentsPerConnection = 8;

  /// 单片目标大小。控制片数不至于过多（每片都有一次 HTTP 请求开销）。
  static const int targetSegmentBytes = 8 * 1024 * 1024;

  /// 进度上报间隔（毫秒）。
  static const int progressIntervalMs = 500;

  /// 速度平滑系数（指数滑动平均）。越小越平滑但越迟钝。
  static const double speedSmoothing = 0.7;

  /// 单个分片的最大重试次数（首次尝试之外）。
  static const int maxSegmentRetries = 4;

  /// 单次任务内允许重新取链的次数上限。
  ///
  /// 必须有上限：若服务端一直回 403（文件被风控、分享被关闭），
  /// 无上限就会变成「取链 → 失败 → 再取链」的死循环。
  static const int maxLinkRefreshes = 5;

  /// 计算分片数量。
  static int planSegmentCount(int totalBytes, int connections) {
    if (totalBytes <= 0) return 1;
    if (totalBytes <= 4 * 1024 * 1024) return 1; // 小文件不分片

    final byConnections = connections * segmentsPerConnection;
    final bySize = (totalBytes / targetSegmentBytes).ceil();
    var count = max(byConnections, bySize);
    count = count.clamp(1, 4096);
    return count;
  }
}
