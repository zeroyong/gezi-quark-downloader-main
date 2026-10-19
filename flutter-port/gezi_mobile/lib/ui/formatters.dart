/// 格式化工具。
class Formatters {
  Formatters._();

  /// 字节数 → 人类可读（1.44 MB）。
  static String bytes(int value) {
    if (value <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    var size = value.toDouble();
    var unit = 0;
    while (size >= 1024 && unit < units.length - 1) {
      size /= 1024;
      unit++;
    }
    final digits = size >= 100 || unit == 0 ? 0 : (size >= 10 ? 1 : 2);
    return '${size.toStringAsFixed(digits)} ${units[unit]}';
  }

  /// 速度 → 人类可读（2.4 MB/s）。
  static String speed(double bytesPerSecond) {
    if (bytesPerSecond <= 0) return '—';
    return '${bytes(bytesPerSecond.round())}/s';
  }

  /// 时长 → 人类可读（1:23 / 1:02:03）。
  static String duration(Duration? d) {
    if (d == null) return '—';
    final total = d.inSeconds;
    if (total <= 0) return '—';
    final h = total ~/ 3600;
    final m = (total % 3600) ~/ 60;
    final s = total % 60;
    if (h > 0) {
      return '$h:${m.toString().padLeft(2, '0')}:${s.toString().padLeft(2, '0')}';
    }
    return '$m:${s.toString().padLeft(2, '0')}';
  }

  /// 时间 → 相对描述（刚刚 / 3 分钟前 / 昨天）。
  static String relativeTime(DateTime time) {
    final diff = DateTime.now().difference(time);
    if (diff.inSeconds < 60) return '刚刚';
    if (diff.inMinutes < 60) return '${diff.inMinutes} 分钟前';
    if (diff.inHours < 24) return '${diff.inHours} 小时前';
    if (diff.inDays == 1) return '昨天';
    if (diff.inDays < 30) return '${diff.inDays} 天前';
    return '${time.year}-${time.month.toString().padLeft(2, '0')}-'
        '${time.day.toString().padLeft(2, '0')}';
  }
}
