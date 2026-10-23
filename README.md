# 鸽子下载自用版（GeZi）

> 一个 Windows 桌面端的**夸克网盘下载器** —— 支持分享链接 / 网盘直链解析、
> 多线程分片下载、边下边播、多账号管理、深色模式。
> 基于 **.NET Framework 4.8 + WPF** 开发，**零 NuGet 依赖、可完全离线构建**。
>
> **当前版本：v2.8.5** · [更新日志](#-更新日志) · [下载](https://github.com/zeroyong/gezi-quark-downloader-main/releases)

<p align="center">
  <img src="src/GeZi/Resources/app-logo.png" width="120" alt="鸽子下载">
</p>
<p align="center">
  <img src="https://img.shields.io/github/repo-size/zeroyong/gezi-quark-downloader-main?style=social" alt="Repo Size">
  <img src="https://visitor-badge.laobi.icu/badge?page_id=zeroyong.gezi-quark-downloader-main&radius=12" alt="Visitors">
  <img src="https://img.shields.io/github/license/zeroyong/gezi-quark-downloader-main?style=social" alt="License">
  <img src="https://img.shields.io/github/stars/zeroyong/gezi-quark-downloader-main?style=social" alt="Stars">
</p>

---

## ✨ 功能特性

### 下载
- **独立分片 + 顺序合并**：每个分片独立文件，**可边下边播**
  （早期版本还有"多线程共写同一文件"模式，因机械盘寻道导致 100% 磁盘占用已移除）
- **高并发分片下载**：片数按文件大小自动计算（≥ 有效并发 × 8，上限 4096），
  单文件最多 **512** 个并发连接
- **连接数按文件大小比例分配**：大文件多给连接，小文件不浪费
- **暂停 / 续传**：断点续传，重启后自动恢复未完成任务
- **直链失效自愈**：直链带时效签名，中途过期会自动换一条新链接着下
- **中转副本没了也能续**：转存下载的文件若中转副本被删除，会自动回到分享里
  重新定位源文件、改走免转存继续下
- **限速自适应**：被服务端限流时自动退避，空闲后自动恢复
- **取消即清理**：取消任务会立刻删除本地残留（想保留进度请用「暂停」）

### 解析与登录
- **分享链接解析**：支持夸克分享链接（含带口令的链接）
- **剪贴板自动解析**：在任何程序里复制到夸克分享链接，本程序就自动解析并载入文件列表
  （默认开启，设置页「剪贴板」可关）
- **解析历史**：按 `h`（或点文件区工具栏的「解析历史」）唤出居中面板，按 今天 / 昨天 / 更早
  分组，点一条即可重新打开那个分享；右键条目可**置顶 / 删除**
- **免转存直链**：文件右键可选「免转存下载」，不占用网盘空间
- **二维码识别**：分享页只给二维码图片时，可截图识别（Ctrl+V / 选文件 / 拖拽）
- **扫码登录**：通过内置的 OpenSSL 通道完成扫码登录与账号信息获取
  （夸克按 TLS 指纹过滤，只有 OpenSSL 3.5.7 能过那道墙，详见「技术说明」）
- **多账号**：可保存多个账号，一键切换

### 体验
- **键盘快捷键**：`1/2/3` 切到下载/任务/登录、`q` 设置、`6` 关于、`h` 解析历史；
  `a` 返回上级、`d` 免转存下载、`x` 下载选中、`g` 获取直链、`s` 多选、`r` 刷新
  （完整列表在设置页最下方「快捷键」一节）
- **大目录不卡**：目录按页加载 —— 一次只渲染 10 条，滑到底部自动续上
- **顺手的目录操作**：单击文件夹即进入；顶部路径条每一节都能点击跳转
- **边下边播**：未下完也能用 VLC 播放已下载部分
- **深色模式**：亮色 / 深色 / 跟随系统
- **托盘常驻 + 悬浮小窗**：关掉主窗口也继续下载
- **任务列表好找事**：可按「失败 / 未完成 / 已完成」分层排序，失败的任务排在最前方便重试；
  失败的任务也能用「全部恢复」一次性批量重试
- **完成后自动关机**：可开启，全部下载完成后弹 15 秒倒计时确认框，
  不操作则自动在 1 分钟内关机
- **导出直链**：纯直链 / curl / aria2c 三种格式
- **拖拽添加**：支持拖拽链接、文件到窗口

---

## 🖥 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | **Windows 10 1903+ / Windows 11**（推荐）<br>Windows 8.1 / Windows 7 SP1 理论可用，需自行安装 .NET Framework 4.8 |
| 运行时 | **.NET Framework 4.8**（Win10 1903 起系统内置） |
| 播放器（可选） | **VLC** —— 仅边下边播功能需要 |
| 架构 | x64 |

> ⚠️ **本软件仅支持 Windows**，不支持 Linux / macOS。
> （UI 基于 WPF、运行时为 .NET Framework，两者均为 Windows 独占。）

---

## 🚀 使用

### 方式一：下载发行版（推荐）

1. 从 [Releases](https://github.com/zeroyong/gezi-quark-downloader-main/releases) 下载 `GeZi-v2.8.5-win64.zip` 并解压到任意目录
2. 双击 `GeZi.exe` 即可运行
3. 若系统缺少 .NET Framework 4.8，Windows 会弹窗提示，按引导安装即可

> **免安装、免 Python**：解压即用。安装包约 **3 MB**（含 OpenSSL 与 ZXing 两个原生库），
> 只含运行必需的文件 —— 调试符号、账号配置一律不进包。

> **数据存放位置**：配置、账号、任务历史统一保存在
> `%LOCALAPPDATA%\鸽子下载\`，**与 exe 所在目录无关**。
> 因此 Debug 版、发行包、放在不同目录的多个副本**共享同一份数据**，
> 换目录启动不会"任务不见了"，也不会出现两份程序互相踩分片。
> 首次启动时若发现旧版把数据放在 exe 同目录（便携模式），会自动迁移过来。

### 方式二：从源码构建

```bash
# 1. 克隆仓库
git clone https://github.com/zeroyong/gezi-quark-downloader-main.git
cd gezi-quark-downloader-main

# 2. 构建（Release）
dotnet build src/GeZi/GeZi.csproj -c Release

# 3. 运行
src/GeZi/bin/Release/net48/GeZi.exe
```

> ⚠️ 建议直接编 `src/GeZi/GeZi.csproj`。仓库里的 `GeZi.slnx` 是新的 XML 解决方案格式，
> 需要 **.NET SDK 9.0.200+**；用较老的 SDK 或只装了 MSBuild 的机器会打不开它。

**无需 `nuget restore`** —— 本项目零 NuGet 依赖，所有第三方库以固化 DLL 形式随仓库分发，
任何机器 clone 下来即可直接构建（需已安装 .NET Framework 4.8 开发包 / MSBuild）。

### 自己打包

```powershell
# 产出 dist/GeZi-v<版本>-win64.zip（只含运行必需文件，体积最小）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package.ps1
```

> ⚠️ 打包**不会**把 `GeZi.config.xml`（含登录 Cookie）与 `accounts/`（账号档案）
> 放进 zip —— 那是本机隐私。现在数据固定存放在 `%LOCALAPPDATA%\鸽子下载\`，
> 发行包解压后**不含任何用户数据**，第一次启动会自动在数据目录里新建配置
> （登录一次，之后就长期保留在那里）。

---

## 🔄 更新（内置双源回退）

程序内置「检查更新」，**无需手动找下载页**：在「关于」页或设置页点「检查更新」，
发现新版本会弹出详情窗，提供两种动作：

- **一键更新**（主按钮）：自动下载安装包 → 解压 → 外部脚本覆盖 → 重启，
  全程无需手动操作（来源支持自动安装时可用）。
- **查看详情**：在浏览器打开来源页，手动下载安装包。

更新源采用**双源回退**，优先 GitHub、GitHub 不可达时回退到夸克网盘分享：

| 源 | 地址 | 说明 |
|---|---|---|
| GitHub Releases（主） | `https://github.com/zeroyong/gezi-quark-downloader-main/releases` | 标准 Release，直链下载 |
| 夸克网盘分享（回退） | `https://pan.quark.cn/s/065af2172c40`（提取码 `VD8m`） | 分享内 `GeZi-vX.Y.Z-win64.zip` |

双源并发检查，取**版本号更高者**；版本相同时优先 GitHub。无论是哪个源，更新都**不会回退到更低版本**。
当前应用版本号从程序集读取（如 `2.8.5`），更新检测据此判断是否有新版本。

> 说明（方案选择）：一键更新采用「解压覆盖 + 外部小脚本接力」的方式 —— 主进程先退出，
> 再由 `gezi-updater.cmd` 用 `xcopy` 覆盖文件并拉起新版本，避免「程序自我覆盖」的占用冲突。

---

## 🔧 技术说明

**为什么内置了 OpenSSL？**
夸克的 `pan.quark.cn` 前置 ALB 按 **TLS ClientHello 指纹**过滤，只放行 ClientHello
足够"大"的客户端。实测（均强制直连）：

| TLS 实现 | 能否访问 `pan.quark.cn` |
|---|---|
| SChannel（.NET HttpClient / Windows curl） | ❌ 连接被重置 |
| OpenSSL 3.0.18（系统 Python 3.14 的 ssl） | ❌ 连接被强制关闭 |
| BouncyCastle 2.7（纯托管 TLS） | ❌ 同上 |
| **OpenSSL 3.5.7（项目内置）** | ✅ HTTP 200 |

因此扫码登录（换登录态）与取账号昵称头像只能走 OpenSSL 这条路。
它是**唯一**的通道，删不得 —— 但压缩后只占约 2 MB，整个安装包仍只有 3 MB。

**为什么曾经内置 Python？**
早期版本用"起一个 CPython 子进程跑 .py"的方式做这件事，代价是包体积 +11 MB，
而且带着「解释器 + 脚本 + 子进程」这组特征 —— 实测被杀毒软件误判。
现在改成 P/Invoke 直调 OpenSSL，**TLS 指纹完全一致，但不再需要解释器**。

---

## 🧱 项目结构

```
GeZi/
├── src/
│   ├── GeZi/                     # 主程序（WPF 界面层）
│   │   ├── MainWindow.xaml(.cs)  # 主窗口
│   │   ├── Resources/            # 主题色、样式、图标
│   │   │   ├── Colors.Light.xaml # 亮色配色（36 个键）
│   │   │   ├── Colors.Dark.xaml  # 深色配色（与亮色键一一对应）
│   │   │   └── ...
│   │   └── ...
│   └── GeZi.Core/                # 核心逻辑（无 UI 依赖，可单独复用）
│       ├── Api/                  # 夸克 API 客户端
│       ├── Download/             # 分片下载引擎
│       ├── Support/              # 二维码编解码、OpenSSL 通道等工具
│       ├── lib/ZXing/            # 固化的 ZXing DLL（二维码解码）
│       └── lib/OpenSSL/          # 固化的 OpenSSL 3.5.7（扫码登录通道）
├── tools/
│   └── package.ps1               # 打包脚本（产出最小体积的发布 zip）
├── LICENSE                       # GPL-3.0
└── THIRD-PARTY-NOTICES.md        # 第三方组件声明
```

---

## 📋 更新日志

> 每条都写清楚「**原来怎样 → 现在怎样**」，方便对比。完整改动见
> [Releases](https://github.com/zeroyong/gezi-quark-downloader-main/releases)。

### v2.8.5（当前）

**修复：关掉应用再打开，点「继续下载」一直 0 B/s**（用户实测，原版五层全有）

这是**五个串联的 bug** —— 修好一层，下一层才露出来：

| # | 原来 | 现在 |
|---|---|---|
| ① | 「已暂停」的任务点 ▶ 只取消暂停信号，**不重新取链**；直链有效期只有 5~10 分钟，跨重启必用死链 → 速度永远 0 | 识别「上次运行恢复出来的任务」，点 ▶ 一律**重新取链再续传** |
| ② | 老任务缺目录路径信息，重新解析只能在分享根目录空转 → 日志只写「自动重新解析也没有成功」，1 秒就结束（一次请求都没发） | 三层兜底：记录路径 → 目录名推断 → **全分享按 fid 搜索**（fid 在同一分享内不变，比路径可靠） |
| ③ | 入队时跨线程修改任务列表 → `NotSupportedException`，整个续传**静默中断** | 入队整体切回 UI 线程，并补外层 `catch` 把异常写进日志 |
| ④ | 启动下载时跨线程改界面 → `InvalidOperationException`，任务停在「排队中」永远不动 | UI 层方法全程 `ConfigureAwait(true)`；`RunJobs` 入口加 `CheckAccess()` 防御 |
| ⑤ | 入队时进度未预填 → 进度条从 0% 跳到实际位置，看着像从头重下 | 入队时读 `.qmeta` 预填真实进度（与启动恢复同一数据源） |

**修复：一键更新失败 / 更新后不自动重启**

| 问题 | 原来 | 现在 |
|---|---|---|
| 🔴 **下载被拒（412）** | 更新流程用「静态 Cookie 快照」新建客户端 → 夸克判为非活跃会话 → 取链成功但下载 412，**连点三次全失败** | **复用主窗口的登录会话**（与手动下载同一条链路），并直接用 `SegmentedDownloader` 下载，不再手写请求 |
| 🟡 **更新后不自动重启** | `start` 继承了隐藏窗口属性 + 脚本自我删除抢跑 → 程序退出就没了 | 改用 `explorer.exe` 代为拉起 |
| 🟡 **日志看不出成败** | 日志跨次累积，同一份里既有 `COPY OK` 又有 `COPY PARTIAL-FAILED` | 每次更新先清空，首行写明时间与 pid；重试成功写明确的 `RETRY-OK` |
| 🟡 **`%TEMP%` 残留** | 删自己正站在里面的目录在 cmd 里不可靠，留下空壳与文件清单 | 改由主程序启动时清理（只删空目录或 12 小时前的） |

**新增**
- **日志右键菜单**：清空日志 / 复制全部（排查时「清屏 → 复现一次 → 只看新日志」）
- **更新日志可随包上传**：分享里与安装包同层放一个 `更新日志.md`，检查到新版本时自动读取并展示

**改进**
- 日志分级：普通用户只看结果，内部细节（扫目录、取链参数）收进「详细」档

### v2.8.4

**修复**

| 问题 | 原来 | 现在 |
|---|---|---|
| 🔴 **配置被写坏**（最严重） | 「已收起分组名」用 `\u0001` 做分隔符，`XmlSerializer` 把它写成 `&#x1;`，而 XML 1.0 禁止该字符引用 → 整份 `config.xml` 变成非法 XML → 下次启动**读不出来**，表现为 **Cookie 丢失（显示未登录）、主题变回亮色、下载目录回到默认值** | 分隔符改为换行符；写配置前剔除非法字符；`Save()` 写完立刻**回读验证**，读不回来就中止并备份原配置，**绝不覆盖**磁盘上的好数据 |
| 🔴 **失败任务点 ▶ 无反应** | 失败态落进 `ResumeTask` 的 `else` 分支，只打一行「该任务当前状态不可恢复（失败）」就结束 —— 行内明明有 ▶ 按钮 | 失败／已取消同样可用 ▶ 恢复：重新取链后**从断点续传**，不重下已下载部分（与「全部恢复」行为一致） |
| 🟡 **「全部收起」只收一部分** | 虚拟化下屏幕外的分组容器不存在，只处理了屏幕内那几个（实测 11 个只收了 3 个） | 「全部收起」改为**写配置**而非操作 UI 容器；每个分组容器生成时自己读配置摆正状态，滚到哪儿都对 |
| 🟡 **收起按钮方向反了** | 重启后全是收起状态，但标志位是 `true`，点一下反而执行「展开」 | 按钮文案与点击方向都由**实际状态**推导，不再自维护 |
| 🟡 **悬浮提示刺眼** | 从未定义 ToolTip 样式，全部使用系统默认的**纯白底黑字**，不跟随主题，暗色界面下弹一块白方块 | 全局隐式 `ToolTip` 样式，颜色走主题令牌 —— 深色主题下是深灰底，浅色主题下是浅灰底 |

**其他**
- 分组展开状态支持**记忆**：收起某个文件夹后重启仍保持收起。
- 加固 `state.json` / `config.xml` 的写入路径：读不到就不写，避免用空模板覆盖真实数据。

### v2.8.3

**修复**
- 🔴 **「一键更新」下载成功但重启仍是旧版本** —— 更新脚本有 4 处路径拼接错误
  （`set APP="...\"` 尾反斜杠截断路径、`AppendLine` 截断路径、`del` 退出码不可靠、
  `for %%F in ("%SRC%\*")` 不展开带引号通配符）。
  现在逐文件复制 + `fc /b` 校验，锁定的文件会跳过并**如实报告** `SKIPPED-FILES`。

### v2.8.2

**改进**
- 数据目录统一到 `%LOCALAPPDATA%\鸽子下载\`：**换目录启动不再「任务不见了」**，
  也不会因为两份副本各写各的而互相踩坏分片。
- 打包产物不再携带用户配置与历史记录。

### v2.8.1 及更早

- 任务跨重启保留、文件夹分组／平铺切换、`z` 批量删除、边下边播、多账号、深色模式等。
- 早期版本还有「多线程共写同一文件」模式，因机械盘寻道导致 **100% 磁盘占用**已移除，
  统一为**独立分片 + 顺序合并**。

---

## 📜 许可证

本项目以 **GNU General Public License v3.0** 发布，详见 [LICENSE](LICENSE)。

```
Copyright (C) 2026  Yi Yuan

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```

这意味着：你可以自由使用、修改、分发本软件，
但**任何分发（含修改版）都必须同样以 GPL-3.0 开源**，并提供完整源代码。

### 第三方组件

本项目使用了以下第三方开源组件，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)：

| 组件 | 许可证 |
|---|---|
| ZXing.Net | Apache-2.0 |
| OpenSSL | Apache-2.0 |

---

## ⚠️ 免责声明

1. 本项目**仅供学习与技术研究**使用。
2. 本项目通过调用**夸克网盘的非公开接口**实现功能，可能随服务端变更而失效。
3. 请勿将本软件用于**商业用途**，或用于**侵犯他人版权 / 违反服务条款**的行为。
4. 使用者应自行承担因使用本软件产生的一切后果，作者不承担任何责任。
5. 下载的内容版权归原权利人所有，请遵守相关法律法规。

---

## 🙏 致谢
感谢 [ZXing.Net](https://github.com/micjahn/ZXing.Net/) 提供的二维码解码能力，
以及 [OpenSSL](https://www.openssl.org/) 项目。

本项目基于 [huaotem-bot/gezi-quark-downloader](https://github.com/huaotem-bot/gezi-quark-downloader)
二次开发（GPL-3.0 允许，原版权与许可声明见 [LICENSE](LICENSE)）。

开发过程中参考了若干开源下载器的公开文档与设计思路（未复制其源代码），
在此一并致谢，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 第 5 节。

---

## ☕ 赞赏支持
如果这个项目对你有帮助，欢迎请作者喝杯咖啡 ☕

<p align="center">
  <img src="xiaozhou.png" width="260" alt="赞赏码">
</p>
