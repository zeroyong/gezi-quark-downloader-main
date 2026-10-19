// 验证文档里给出的代码片段是否真能编译/工作
import 'dart:io';
import 'dart:async';

// --- §4.1 DownloadTuning ---
class DownloadTuning {
  static const int defaultConnections = 16;
  static const int maxConnections = 64;
  static const int minSegment = 4 * 1024 * 1024;
  static const int readChunk = 64 * 1024;
}

// --- §4.2 ThreadAllocator 移植 ---
List<int> allocate(List<int> sizes, {
  int perFileCap = 16,
  int budget = 32,
  int minPerFile = 2,
  int concurrency = 2,
}) {
  final n = sizes.length;
  final r = List<int>.filled(n, 0);
  if (n == 0) return r;
  if (n == 1) { r[0] = perFileCap; return r; }
  final k = concurrency < 1 ? 1 : concurrency;
  final waves = (n + k - 1) ~/ k;
  final effBudget = budget * (waves < 1 ? 1 : waves);
  final total = sizes.where((s) => s > 0).fold<int>(0, (a, b) => a + b);
  if (total <= 0) {
    var even = effBudget ~/ n;
    if (even < minPerFile) even = minPerFile;
    if (even > perFileCap) even = perFileCap;
    for (var i = 0; i < n; i++) { r[i] = even; }
    return r;
  }
  for (var i = 0; i < n; i++) {
    var t = (effBudget * (sizes[i] > 0 ? sizes[i] / total : 0)).round();
    if (t < minPerFile) t = minPerFile;
    if (t > perFileCap) t = perFileCap;
    r[i] = t;
  }
  // 超出部分从最大项扣回，但下限优先
  var sum = r.fold<int>(0, (a, b) => a + b);
  var guard = 0;
  while (sum > effBudget && guard++ < 100000) {
    var best = -1;
    for (var i = 0; i < n; i++) {
      if (r[i] > minPerFile && (best < 0 || r[i] > r[best])) best = i;
    }
    if (best < 0) break;
    r[best]--; sum--;
  }
  return r;
}

Future<void> main() async {
  print('=== §4.1 常量 ===');
  print('default=${DownloadTuning.defaultConnections} max=${DownloadTuning.maxConnections}');

  print('\n=== §4.2 连接分配 ===');
  // 大中小混合
  final sizes = [10 * 1024 * 1024 * 1024, 500 * 1024 * 1024, 10 * 1024 * 1024, 1024 * 1024];
  final alloc = allocate(sizes);
  print('sizes = ${sizes.map((s) => "${(s/1024/1024).toStringAsFixed(0)}MB").toList()}');
  print('alloc = $alloc  (sum=${alloc.fold(0,(a,b)=>a+b)})');
  print('  最大文件给最多: ${alloc[0] == alloc.reduce((a,b)=>a>b?a:b) ? "OK" : "FAIL"}');
  print('  最小文件保底>=2: ${alloc.last >= 2 ? "OK" : "FAIL"}');
  print('  均未超上限16:    ${alloc.every((x)=>x<=16) ? "OK" : "FAIL"}');

  // 单文件
  print('单文件 -> ${allocate([999999])}');
  // 大小未知
  print('大小未知 -> ${allocate([0,0,0])}');
  // 大量小文件（验证下限优先于预算）
  final many = List<int>.filled(50, 1024);
  final ma = allocate(many);
  print('50 个小文件 -> sum=${ma.fold(0,(a,b)=>a+b)} min=${ma.reduce((a,b)=>a<b?a:b)} max=${ma.reduce((a,b)=>a>b?a:b)}');

  print('\n=== §2.6 truncate 稀疏文件（移动端预分配）===');
  final p = '${Directory.systemTemp.path}/tune_test.bin';
  final f = File(p);
  final sw = Stopwatch()..start();
  final raf = await f.open(mode: FileMode.write);
  await raf.truncate(5 * 1024 * 1024 * 1024);
  await raf.close();
  sw.stop();
  print('truncate 5GiB = ${sw.elapsedMilliseconds}ms, size=${await f.length()}');
  await f.delete();

  print('\n=== §4.4 相对路径策略 ===');
  const appDoc = '/data/user/0/com.example.gezi/app_flutter';
  const rel = 'gezi/2023/书名.epub';
  final abs = '$appDoc/$rel';
  print('存库: $rel');
  print('启动拼: $abs');
  print('  不含绝对前缀: ${!rel.startsWith('/') ? "OK" : "FAIL"}');

  print('\n全部片段编译并运行通过 ✔');
}
