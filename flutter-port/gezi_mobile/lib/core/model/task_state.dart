/// 下载任务的状态机。
///
/// 移植自桌面版 `DownloadScheduler.cs` 的 `JobState`，
/// 但去掉了桌面版特有的 `Cancelling` 中间态（移动端用 UI 层自行处理）。
enum TaskState {
  /// 已入队，等待调度。
  queued,

  /// 正在下载。
  downloading,

  /// 用户暂停。
  paused,

  /// 已完成。
  completed,

  /// 失败（可重试）。
  failed,

  /// 已取消（用户主动放弃）。
  cancelled;

  String get label {
    switch (this) {
      case TaskState.queued:
        return '等待中';
      case TaskState.downloading:
        return '下载中';
      case TaskState.paused:
        return '已暂停';
      case TaskState.completed:
        return '已完成';
      case TaskState.failed:
        return '失败';
      case TaskState.cancelled:
        return '已取消';
    }
  }

  /// 是否为「结束态」（不再占用调度名额）。
  bool get isTerminal =>
      this == TaskState.completed ||
      this == TaskState.cancelled ||
      this == TaskState.failed;

  /// 是否可以继续/重试。
  bool get canResume =>
      this == TaskState.paused ||
      this == TaskState.failed ||
      this == TaskState.queued;

  static TaskState fromName(String name) {
    for (final s in TaskState.values) {
      if (s.name == name) return s;
    }
    return TaskState.queued;
  }
}

/// 续传上下文：让任务在 App 重启后能重新取链并续下。
///
/// 对应桌面版 `PendingInfo`。
///
/// 除 [destPath] 外的字段都用于**重新申请直链** ——
/// 旧直链带时效签名，重启时必然已失效。
class ResumeContext {
  /// 分享 ID（分享模式才有）。
  final String? pwdId;

  /// 分享 stoken（分享模式才有）。
  ///
  /// ⚠️ 注意：桌面版有个已知限制 —— stoken 只在解析分享时取一次，
  /// 续传时不重新获取。stoken 过期后就永远取不到新链。
  /// 移动端这里记录它，并在续传失败时提示用户「重新解析分享」。
  final String? stoken;

  /// 提取码。
  final String? passcode;

  /// 网盘文件 fid。
  final String fid;

  /// 分享内文件的 token（分享模式才有）。
  final String? shareToken;

  /// 是否来自分享（否则为「我的网盘」）。
  final bool fromShare;

  const ResumeContext({
    this.pwdId,
    this.stoken,
    this.passcode,
    required this.fid,
    this.shareToken,
    this.fromShare = true,
  });

  /// 是否具备重取直链所需的全部信息。
  bool get canResume {
    if (fid.isEmpty) return false;
    if (!fromShare) return true; // 我的网盘：只需 fid
    return (pwdId?.isNotEmpty ?? false) &&
        (stoken?.isNotEmpty ?? false) &&
        (shareToken?.isNotEmpty ?? false);
  }

  Map<String, dynamic> toJson() => <String, dynamic>{
        if (pwdId != null) 'pwdId': pwdId,
        if (stoken != null) 'stoken': stoken,
        if (passcode != null) 'passcode': passcode,
        'fid': fid,
        if (shareToken != null) 'shareToken': shareToken,
        'fromShare': fromShare,
      };

  factory ResumeContext.fromJson(Map<String, dynamic> json) => ResumeContext(
        pwdId: json['pwdId']?.toString(),
        stoken: json['stoken']?.toString(),
        passcode: json['passcode']?.toString(),
        fid: (json['fid'] ?? '').toString(),
        shareToken: json['shareToken']?.toString(),
        fromShare: json['fromShare'] == true,
      );
}
