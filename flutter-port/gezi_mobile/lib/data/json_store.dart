import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// 原子化的 JSON 文件存储。
///
/// ============================================================================
/// 【为什么要"原子"】
///
/// 移动端进程被系统杀死是常态（切后台、内存压力、用户划掉）。
/// 如果直接 `file.writeAsString(json)`，写到一半被杀 → 文件只剩半截 JSON →
/// 下次启动解析失败 → **全部任务记录丢失**。
///
/// 做法：写临时文件 → fsync → rename 覆盖。
/// rename 在同一文件系统内是原子的（POSIX 与 NTFS 都保证），
/// 所以目标文件要么是完整的旧版本，要么是完整的新版本，不存在中间态。
/// ============================================================================
class JsonStore {
  final File file;

  JsonStore(this.file);

  /// 读取 JSON。文件不存在/损坏时返回 null（不抛异常 —— 损坏的存档
  /// 不该让 App 起不来，宁可当作用户第一次使用）。
  Future<Map<String, dynamic>?> read() async {
    try {
      if (!await file.exists()) return null;
      final text = await file.readAsString();
      if (text.trim().isEmpty) return null;
      final decoded = jsonDecode(text);
      if (decoded is Map<String, dynamic>) return decoded;
      if (decoded is Map) return decoded.cast<String, dynamic>();
      return null;
    } catch (_) {
      return null;
    }
  }

  /// 原子写入 JSON。返回是否成功。
  Future<bool> write(Map<String, dynamic> data) async {
    try {
      await file.parent.create(recursive: true);

      final tmp = File('${file.path}.tmp');
      final raf = await tmp.open(mode: FileMode.write);
      try {
        final bytes = utf8.encode(const JsonEncoder.withIndent('  ').convert(data));
        await raf.writeFrom(bytes);
        // flush 确保数据真的落到存储介质，而不是只停在 OS 缓存里。
        // 少了这一步，"rename 成功但内容是空的"这种情况在断电/强杀后会出现。
        await raf.flush();
      } finally {
        await raf.close();
      }

      // rename 覆盖：同一文件系统内原子
      await tmp.rename(file.path);
      return true;
    } catch (_) {
      return false;
    }
  }

  /// 删除存档。
  Future<void> delete() async {
    try {
      if (await file.exists()) await file.delete();
    } catch (_) {
      // 删不掉也不影响功能
    }
  }
}
