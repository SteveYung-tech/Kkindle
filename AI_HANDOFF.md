# Kkindle 项目交接文档

> 给后续 AI / 开发者使用。继续工作前请先阅读本文档，再查看代码和当前 Git 状态。
>
> 更新时间：2026-08-17
>
> 项目目录：`C:\Users\kings\Desktop\01_Projects\Kkindle`

> 迁移基线（2026-08-15）：Avalonia 阶段 2 至阶段 7 均已完成并合入 `master`，包括三端平台层/启动头、Calibre 去捆绑、发布体系和窗口关闭默认退书架。WinUI 版本继续保留作迁移参照。
>
> 当前 Kreader 状态（2026-08-17）：已修正 Windows 右边框、白底书签页与只在当前可见页显示的书签角标、完整/极简目录及子章节、单页/双栏/滚动模式键鼠导航、章节标题首行定位、EPUB 进度恢复与目录提取。正文分页重新按 WinUI 旧版约束校准，前后翻页、首次渲染左移和章节边界已修复；左右滑动由新页从右向左覆盖旧页，水波使用当前页面快照复刻 Kindle 灰度刷新前沿（560ms）。脚注改由 WebView2 之上的原生 Popup 在脚注附近显示，清理了悬停时的本地 XHTML 地址；Windows 通过 WebView2 `ICoreWebView2Settings.IsStatusBarEnabled=false` 关闭浏览器状态栏。极简目录、禅模式按钮和章节浮窗均使用原生 Popup，已消除 HWND 覆盖造成的文字缺失与闪烁。快速输入仍由只保留最新方向的非递归串行消费者处理。尚待 Linux/macOS 真实桌面与真实 Kindle 验收。

## 0. 当前状态

- 基线功能（WinUI 参考版）：P0/P1/P2、本地书库、Kreader 阅读器、阅读资料中心、Kindle 设备管理（USB/WPD/MTP）、格式转换、Z-Library、Kindle 邮件、备份/设置、AI 助手、安装包与 GitHub 自动发版均已实现并验证；Avalonia 当前版已完成阶段 2/3 与阶段 4 全量移植，但不能据此视为已与 WinUI 全部等价，具体对等状态见第 10.8 节。
- 分支为 `master`，远端为 `git@github.com:kingstacker/Kkindle.git`；2026-08-17 本次交接更新包含 Kreader 正文分页、前后翻页、书签可见页判定、脚注 Popup、禅模式原生控件、左右滑动/水波动画、WebView2 状态栏关闭，以及跨平台 CI/发布脚本更新。`v0.5.2`（`5daf140`）仍是最新正式标签；本次工作提交源码并生成本地调试 EXE，不创建版本标签或 GitHub Release。
- 最新版本：0.5.2（标签 `v0.5.2`）；在 0.5.1 基础上新增全应用滚动条自动隐藏，滚动或悬停时显示，空闲后淡出；补齐 Popup、折叠面板、ContentDialog 和延迟生成模板的挂载，并隔离嵌套 ScrollViewer 的滚动条归属。
- 测试：Debug 2026-08-17 共 **240 项**，分布在三个项目：`Kkindle.Tests` 206 项（`net8.0`）+ `Kkindle.Tests.Windows` 28 项（`net8.0-windows`）+ `Kkindle.Platform.Common.Tests` 6 项（`net8.0`）。平台公共测试必须按临时挂载根匹配测试设备，不能假设机器上没有真实 Kindle。
- 当前 Avalonia Windows 单文件调试 EXE：`artifacts\Kkindle-debug.exe`，2026-08-17 构建为自包含 win-x64 单文件（202,744,633 字节，SHA-256 `494DAAE7AF9A75F7D4254EAE832E04FB245D65272C73382E93108B44E0A81B9F`）；完整 240 项测试通过，用户确认打开书籍不再闪退。该 EXE 是本地产物，不提交 Git。
- 本地旧版完整测试包（2026-08-12 19:36，版本 `0.5.0-test.1`，由 `685ab20` 发布；该历史包曾内置 Calibre 与 KFX Input，新发布策略已禁止捆绑 Calibre）：
  - exe：`artifacts\Kkindle-0.5.0-test.1\Kkindle-0.5.0-test.1-win-x64\Kkindle.exe`
  - 便携包：`artifacts\Kkindle-0.5.0-test.1\Kkindle-0.5.0-test.1-win-x64-portable.zip`
- Windows 常规发布目录：`src\Kkindle.Desktop.Windows\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`（旧 `Kkindle.App`/WinUI 发布目录已废弃；如需随最新提交刷新请重新发布）。
- 真机验证（Windows）：Kindle Scribe（MTP）EPUB 发送/扫描/删除闭环、64 MiB 大文件传输、设备字体/字典读写均已验收，设备端无测试残留。
- 开发约定：代码修改必须能编译；每次发布 EXE 只创建一个对应 Git 提交；文档随代码一并提交。
- 已启动 WinUI 3 → Avalonia 迁移（Windows 优先，架构上预留 Linux/Mac），计划与进度见第 10 节；阶段 4 功能对等缺口已全量移植（见 10.5），阶段 7 三端落地已提交，WinUI 版继续保留。

## 1. 项目目标与技术路线

Kkindle 是只供个人使用的个人电子书与 Kindle 设备管理器，面向 Windows、Linux 与 macOS：

- 视觉：白底黑字纸张感、零圆角硬边矩形、黑白为主、无渐变/强阴影；全自绘黑白标题栏，Windows 上 DWM 禁用窗口圆角。
- 布局：左侧导航、中间书架、右侧详情；默认中文；本地书库与 Kindle 书库独立入口。
- 技术：C# + .NET 8、Avalonia 12.1.1 + `Avalonia.Controls.WebView`（官方第一方包；Windows 走 WebView2，Linux 走 WebKitGTK 4.1 / WPE WebKit 2.0，macOS 走系统 WebView）、CommunityToolkit.Mvvm 8.4.2、SQLite（Microsoft.Data.Sqlite）；三端各自一个瘦桌面启动头（`Kkindle.Desktop.*`），平台能力通过 `AppServices` record 注入可移植的 `Kkindle.App`；Windows 保留 WPD/MTP + DPAPI，Linux 用 Secret Service（`secret-tool`），macOS 用登录钥匙串。

## 2. 项目结构

```text
Kkindle/
├─ Kkindle.sln / Directory.Build.props
├─ README.md / AI_HANDOFF.md / LICENSE
├─ scripts\Build-Release.ps1       # Windows 统一发布脚本（便携包 + 安装包 + 校验和）
├─ scripts\build-linux-release.sh  # Linux .deb + tar.gz 发布脚本
├─ scripts\build-macos-release.sh  # macOS .app 打包 + 签名（可选公证）脚本
├─ installer\Kkindle.iss           # Inno Setup 6 安装包脚本（仅 Windows）
├─ .github\workflows\release.yml       # 推送 vX.Y.Z 标签：三端构建 + 聚合 GitHub Release
├─ .github\workflows\cross-platform.yml# PR / 手动触发的 Linux·macOS 构建与打包校验
├─ docs\cross-platform.md          # Linux/macOS 数据目录、密钥、构建与发布说明
├─ src\Kkindle.App/                # Avalonia 跨平台 UI（窗口、页面、阅读器宿主与注入脚本）
├─ src\Kkindle.Core/               # 领域模型、策略（阅读选择/导航/分页/转换/设备型号目录）
├─ src\Kkindle.Infrastructure/     # SQLite、元数据、Calibre 定位/安装、AI、备份、Z-Library
├─ src\Kkindle.Platform.Common/    # 挂载 U 盘 Kindle 设备服务 + AES-GCM 密钥保护基类（net8.0）
├─ src\Kkindle.Platform.Windows/   # WPD/MTP、DPAPI（WindowsSecretProtector）、WM_DEVICECHANGE
├─ src\Kkindle.Platform.Linux/     # Secret Service、XDG 数据目录、udisksctl 弹出
├─ src\Kkindle.Platform.MacOS/     # Keychain、Application Support 数据目录、diskutil 弹出
├─ src\Kkindle.Desktop.Windows/    # Avalonia Windows 启动头（WinExe，只做 DI 装配）
├─ src\Kkindle.Desktop.Linux/      # Avalonia Linux 启动头
├─ src\Kkindle.Desktop.MacOS/      # Avalonia macOS 启动头
├─ src\Kkindle.App.WinUI/          # WinUI 3 参考实现，作为旧版迁移基线（对等后删除）
├─ tests\Kkindle.Tests/            # 可移植单元测试（net8.0）
├─ tests\Kkindle.Platform.Common.Tests/ # 平台公共层测试（net8.0）
└─ tests\Kkindle.Tests.Windows/    # 设备测试（net8.0-windows，WPD/MTP，只能 Windows 跑）
```

应用数据目录：Windows 在 exe 旁（`data\{library,covers,reader-cache,logs,kkindle.db}`、`backups`、`app-root.json`）；Linux 遵循 XDG（`$XDG_DATA_HOME/Kkindle`、配置在 `$XDG_CONFIG_HOME/Kkindle`）；macOS 在 `~/Library/Application Support/Kkindle`。三端均可在设置中迁移数据目录。

## 3. 已实现功能

> 本节是 WinUI 参考版的完整功能基线；Avalonia 迁移版是否已逐项对等，以第 10.8 节的审计结果为准。

### 3.1 本地书库

- 导入文件/文件夹（EPUB/PDF/MOBI/AZW3），复制进数据目录，SHA-256 去重、同书多格式合并；单个文件失败不影响整批导入。
- 元数据解析与编辑（标题/作者/系列/标签/简介/封面）；搜索、作者/标签/格式/分类/阅读状态筛选、收藏、排序；书架/列表/网格画廊三种视图。
- 多格式打开子菜单（按 EPUB/PDF/AZW3 优先级）、按格式删除、一键删除全部（二次确认；阅读中先关闭阅读器）。

### 3.2 Kreader 阅读器

- 三栏布局（目录/正文/阅读助手），目录支持完整/极简两种模式；窗口过窄自动收起侧栏。
- 支持 EPUB、PDF（WebView 内置查看器）、AZW3（自动转临时 EPUB）；EPUB 自带脚本在落盘消毒阶段剥离（去 script/iframe/on* 属性 + CSP nonce）。
- 滚动/分页/双页/竖排（仅滚动模式）/翻页动画（无、淡入淡出、左右滑动、水波流动）。
- 每书独立排版设置（字号/行高/正文宽度/边距/字体/CJK 覆盖/竖排）持久化；进度断点恢复；书签；书内搜索（FTS + LIKE 回退，带高亮）；划线/批注；脚注悬停浮窗；阅读统计；AI 助手；禅模式。
- 双 WebView 下一章预加载；禅模式为真全屏（Windows FullScreen presenter / Avalonia 全屏窗口，F11 进入 / Esc 退出，chrome 自动隐藏）。
- **窗口关闭默认先退书架**：阅读中点击标题栏 X（或 Alt+F4 等平台关闭请求）只关闭 Kreader、回到主界面，再次点击才退出应用；阅读器内「返回书架」与禅模式 × 行为一致。Avalonia（`MainWindow_Closing` + `CloseWindowButton_Click`）与 WinUI 参照版（`AppWindow_Closing` + `CloseWindowButton_Click`）已同步实现。
- 目录/子章节 fragment 跳转顶格、章节首行归一化、分页列边界吸附（详见第 4 节约束）。
- EPUB3 `nav`、EPUB2 OPF `guide` 与 NCX 目录按可靠性提取；错误 NCX 可由书内目录页回退。目录项、底部章节按钮和进度滑块统一使用语义目录（含同 XHTML 内 fragment 子章节），当前正文会选中对应目录项；已有阅读进度恢复到保存位置，无进度书籍从封面开始。
- 单页与双栏 EPUB：上/下键切换上/下语义章节（支持子章节），左/右键切换上/下物理页；PDF 的方向键按页切换；滚动模式左/右键切章节，上/下键滚动。同一连续滚轮手势只滚到章节边缘，停顿后再次滚动才接上/下章。章节跳转把章节名定位为正文第一行。
- 页内书签入口位于正文右上角热区，当前页有书签时显示嵌入右上角的黑色三角；左侧目录页可覆盖切换到纯白背景的书签详情，不显示章节总数。极简目录选中/悬停项及底部固定 `12×12` 圆形进度 thumb 共用原生章节浮窗，鼠标离开即关闭；章节位置只显示在右下角。单页/双栏/滚动模式切换状态显示 2.5 秒后自动清空。
- 所有键盘翻页/切章均经 `_readerPageTurnGate` 和 `_readerPendingKeyboardNavigation`：只保留最新方向、非递归串行消费。禁止从 WebView `key` 消息或 Avalonia 焦点回退路径直接调用 `MoveReaderChapterAsync`，否则快速按键会重新引入并发导航。
- “更多”菜单三个入口按 WinUI 参照版显式关闭菜单。阅读排版设置必须使用 `ReaderLayoutSettingsPopup`（原生 Popup），不能退回普通 Grid 覆盖层，否则 Windows WebView2 HWND 会压住白色设置面板；禅模式的窗口阴影边距在 `FullScreen` 下必须归零；翻页动画只作用于分页模式，左右滑动使用整页相反方向的移出/移入。
- 脚注浮窗由 `ReaderFootnoteHostPopup` 托管，位置使用 WebView 消息中的指针坐标并限制在正文视口内；不得改回普通 Grid/Border 覆盖层。脚注链接的 `href` 在悬停时临时移入 `data-kkindle-footnote-href`，点击解析仍使用保存值，避免 WebView2 显示原始 `.xhtml#fragment` 地址。
- 页切换动画优先通过 `IReaderPageSnapshotProvider` 捕获当前 WebView 视口：左右滑动保持旧页静止、新页按方向覆盖；水波使用 560ms 灰度高对比刷新前沿。平台无法截图时才回退普通淡入。
- Kreader 全书搜索结果使用右侧固定槽位中的原生浅灰滚动条，保留上下三角按钮和可拖动滑块；滚动/悬停显示、闲置隐藏，不显示滑轨线。搜索框与结果内容左边距对齐，结果列表仍扩到目录栏边缘以固定原生滚动条位置；每个词条矩形右侧内缩 `14` DIP，与上方搜索框右边界对齐，搜索结果项自身不再显示全局 `ListViewItem` 的额外外框，避免右侧出现多余矩形；滚动条只位于搜索结果区，不影响底部“返回书架”矩形。搜索框文字垂直居中，词条矩形使用更细的 `0.5` DIP 边框。左侧底部按钮铺满整块底栏、与右侧底栏同高，外框采用与右侧底栏一致的 `#E2E2DE` 浅灰线。

### 3.3 阅读资料中心

- 统一汇总本地全部书籍的划线/批注/页级批注 + 已连接 Kindle 的 `My Clippings.txt`；按来源筛选、全文搜索、Markdown/纯文本导出、回到原文定位、逐条删除。
- Kindle 笔记删除仅改写 `My Clippings.txt`，不修改书籍侧车数据库或云端标注。

### 3.4 Kindle 设备

- **Windows**：USB 磁盘 + WPD/MTP 识别；容量、书籍扫描、发送（临时文件 + SHA-256 校验 + 原子改名 + 同名编号）、删除（`IFileOperation`，路径白名单）、安全弹出；`WM_DEVICECHANGE` + 3 秒轮询兜底；设备断开自动取消并精确清理本次文件。
- **Linux/macOS**（`Kkindle.Platform.Common/MassStorageKindleDeviceService`）：识别挂载为文件系统的 Kindle（`/media/<user>`、`/run/media/<user>`、`/Volumes` 及可移动盘符下含 `documents` 的目录）；书籍扫描/发送（临时文件 + SHA-256 校验 + 原子改名 + 同名编号）/删除/导出、字体与字典资源读写、`My Clippings.txt` 读写与笔记删除、缩略图写入；安全弹出分别走 `udisksctl unmount`（回退 `umount`）与 `diskutil eject`；链接（reparse point/symlink）与路径逃逸白名单防护。MTP-only Kindle 在这两平台暂不枚举。
- 设备字体（`fonts` 的 TTF/OTF）与字典（`documents\dictionaries` 的 AZW/AZW3/MOBI/KFX）读取/导入/导出/删除。
- 设备身份用 USB 卷序列号 / WPD shell 路径（Windows）或挂载根路径（Linux/macOS），盘符变化不影响识别；设备型号记忆（`DeviceModelStore` + `DeviceModelCatalog`，内置 Kindle/汉王/掌阅/Kobo 型号，支持自定义）。

### 3.5 格式转换

- Calibre `ebook-convert`：EPUB/AZW3/PDF 互转，KFX→EPUB（需 Calibre 已装 KFX Input 插件，不支持绕过 DRM）；结果写回原书；实时进度、后台、取消。
- **三端发布包均不再捆绑 Calibre/KFX Input**。`CalibreExecutableLocator`（`Kkindle.Infrastructure`）按平台发现 `ebook-convert`：用户指定（设置项 / `KKINDLE_CALIBRE_CONVERT`）→ exe 旁 `Calibre`/`Calibre2` → 平台标准位置（Windows Program Files 的 Calibre2、Linux `/usr/bin` 等 + `~/calibre-bin`、macOS `calibre.app`/Homebrew）→ `PATH`；Linux/macOS 可执行文件无 `.exe` 后缀。
- **用户手动安装**（设置页按钮，`CalibreSetupService`）：Windows 下载官方签名 MSI 并启动安装；Linux 运行 calibre 官方隔离安装器到 `~/calibre-bin`（无需 root）；macOS 校验官方 DMG 与应用签名后放入 `~/Applications`。KFX Input 从 calibre 官方插件索引下载、校验插件 ZIP 结构，用检测到的 `calibre-customize` 安装；仅当 Kkindle 自己安装了 KFX 插件时才使用独立 `CALIBRE_CONFIG_DIRECTORY` 隔离配置，避免污染用户 Calibre 配置。所有下载都不进入 Kkindle 发行产物。

### 3.6 Z-Library 与 Kindle 邮件

- Z-Library eapi 搜索/下载（格式/语言筛选、分页、自动入库去重、临时文件清理）；账号凭据加密保存；API 地址可配置镜像。
- SMTP 发送 EPUB/PDF 到 Kindle 个人文档邮箱；SMTP 密码不写入备份包。

### 3.7 备份、设置与 AI

- `.kkindle` 备份导出/导入/迁移（书库/封面/阅读记录），每日自动备份与保留数量；API Key 与 SMTP 密码不入备份。
- 设置：默认打开格式、默认排版、Calibre 路径、AI/网络权限、数据目录；实时自动保存。Calibre 设置页新增「下载/安装 Calibre」与「安装 KFX Input」按钮及进度条（需开启网络功能）。
- 密钥存储统一走 `ISecretProtector`：Windows 用 DPAPI（`WindowsSecretProtector`，blob 字节兼容旧版）；Linux/macOS 用 `AesGcmSecretProtector` 基类 + 平台密钥存储（Linux `secret-tool` Secret Service，macOS 登录钥匙串），AES-GCM 加密 blob 带 `0x4B4B5301` 头与随机 nonce。
- AI 助手：DeepSeek / OpenAI / 兼容接口，SSE 流式，思考深度与模型选择，本地书库索引检索上下文，选区解释、全书概览、书内问答；对话只发送相关片段，不上传整本书。

## 4. 关键技术约束（必读）

1. **WebView 安全与 COM 基线**：EPUB 在落盘阶段剥离 `<script>`、`on*` 属性、`javascript:`/`data:` URL 与外部资源，注入 CSP `<meta>`（`default-src 'none'` + 自研 nonce）只放行自己的注入脚本；导航白名单限当前 EPUB 缓存/PDF；`EnableDevTools=false`；右键菜单由页面级 `contextmenu` preventDefault 取消。Windows 启动头只用本地最小 `[ComImport]` 声明调用 `ICoreWebView2.get_Settings` 和 `ICoreWebView2Settings.put_IsStatusBarEnabled(FALSE)`；**绝对不要对 Avalonia 提供的指针调用 `Marshal.ReleaseComObject`，也不要恢复反射 Avalonia 内部 COM 类型的实现**，否则会破坏 WebView2 所有权并在打开书籍时原生闪退。
2. **分页 CSS 数学**：`html { overflow:hidden }` 是唯一滚动容器，`body` 必须 `overflow:visible`；分页显式使用 `column-count: 1`，双页/双栏显式使用 `column-count: 2`，`column-width: auto`，不要恢复由 Chromium/DPI 推断列数的计算式。正文两侧 padding 与 `column-gap` 共同保持一屏边界；翻页/吸附统一用 `scrollingElement.clientWidth` 步进，`SnapReaderPaginationAsync()` 钉死 `top:0`。
3. **图片/封面**：`img/svg` `max-width:100%`，并用内容盒高度变量 `--kkindle-page-content-h` 做 `max-height` contain 拟合，分页/滚动各自适配，避免裁切或横向溢出。
4. **导航意图与守卫**：`ReaderNavigationIntent`（None/Toc/Progress/Bookmark/Annotation/Search/AiSource）+ `PruneReaderPendingLocations` 只保留本次意图的 pending 位置；导航序列守卫（`_readerChapterTransitionSequence` + 取消令牌 + `_readerCloseRequested`）保证旧导航/旧后置任务不覆盖新章节。普通章节跳转先 `NormalizeReaderChapterStartAsync`（删开头空白节点、首个有效内容元素 margin-top 归零）；fragment 用 `.kkindle-fragment-break`（`break-before: column !important`，**禁止再混入 `page-break-before`**），滚动模式按正文内容盒顶部对齐；带 fragment/书签/批注/搜索/AI 目标不归一化。
5. **章节切换与关闭**：导航期间保持旧内容可见、首屏完成后再短淡入；非首屏任务延迟执行并随时检查守卫；目录/搜索/书签/批注跳转一律短淡入。关闭流程幂等非阻塞：先停钩子/轮询/计时器 → 有界异步落库（不碰 WebView）→ 清理；重复点击 X / 返回书架 / 关窗均安全。**窗口级关闭（标题栏 X / Alt+F4）在阅读中默认只关闭阅读器退回主界面**，见 3.2。
6. **XAML 启动坑**：`Slider`/`ComboBox` 的 `ValueChanged`/`SelectionChanged` 会在 XAML 解析给属性赋值时提前触发，事件处理器必须加 `AreReaderLayoutControlsReady()` 空值守卫，否则启动崩溃。
7. **窗口 chrome（WinUI 参照版）**：`ConfigureTitleBar()` 不得在窗口激活前读 `AppWindow`；`ConfigureNativeWindowChrome()` 只在首次 `Window.Activated` 后调用；`ApplySquareWindowFrame()` 的多次调用时机必须保留，否则 Windows 恢复圆角。Avalonia 版为 `WindowDecorations=None` + `ExtendClientAreaToDecorationsHint`，主界面左右侧按黄金分割。
8. **阅读器脚本模块**：导航/分页/外观/水波分别在 `ReaderNavigationScripts.cs`、`ReaderPaginationScripts.cs`、`ReaderAppearanceScripts.cs`、`ReaderWaveScripts.cs`；动画参数（水波 560ms、32 条带等）只改常量。章节切换的覆盖层使用 PNG data URL，页内切换优先使用 View Transition；两者都必须遵守“新页覆盖旧页”的方向语义。Avalonia 与 WinUI 共享同一份脚本文件（`Kkindle.App.csproj` 以链接方式引入）。
9. **SQLite**：新表一律 `CREATE TABLE IF NOT EXISTS` 幂等；旧表加列用 `PRAGMA table_info` + `ALTER TABLE`（如 `TwoPageMode`）；`ReaderDataService.InitializeAsync()` 幂等。
10. **内置字体**：京华老宋体 v3.0（33,259,644 字节，SHA-256 `F7FEF9FC413E9E2343F0BB432C51CCA41C44B8FE37F071DC86B050896AE9F9E2`），Avalonia 走 `avares://` 资源，EPUB 走 `@font-face`（file:// URL）。
11. **其他**：Avalonia `App.axaml` 的资源合并层级是启动稳定基础，不要改动；源码统一 UTF-8；不要根据终端显示乱码直接判断业务字符串损坏。
12. **默认调试产物**：除非用户明确要求 Release、安装包、便携包或正式发布，否则只生成 x64 Debug EXE；必须保留完整调试工具和运行依赖，不做裁剪或精简，方便调试。
13. **界面视觉限制**：除非用户明确要求，界面严格只使用黑、白、灰三色；按钮使用直角矩形；开关使用黑白样式；不得擅自引入圆角、彩色、渐变或强阴影。
14. **Calibre 定位与安装**：所有 Calibre 可执行文件路径一律经 `CalibreExecutableLocator`（`DesktopOperatingSystem` 分支，`ebook-convert` 无 `.exe` 后缀）；不要恢复发布包捆绑 Calibre/KFX Input 的逻辑，也不要写死 `.exe` 路径。KFX 插件若由 Kkindle 安装才启用独立 `CALIBRE_CONFIG_DIRECTORY`，否则直接使用用户 Calibre 配置。
15. **Linux WebView 引擎适配**：`NativeWebViewReaderHost.View_EnvironmentRequested` 在 Linux 上先探测 `libWPEWebKit-2.0.so.1`，能加载则保持 WPE 适配器，否则 `PreferWebKitGtkInstead` 切到 WebKitGTK 4.1（.deb 已依赖 `libwebkit2gtk-4.1-0`）。阅读器引擎差异（字体注入、PDF、滚轮等）需在真实 Linux/macOS 桌面人工复核。
16. **数据/配置目录分离**：`AppRootConfiguration.ResolveRoot(configurationDirectory, fallbackRoot)` 的配置目录与数据目录分离（Windows 均为 exe 旁；Linux 配置 `$XDG_CONFIG_HOME/Kkindle`、数据 `$XDG_DATA_HOME/Kkindle`；macOS 均为 `~/Library/Application Support/Kkindle`）。平台头通过 `AppServices.Paths`/`RootConfigurationDirectory` 注入，`App.axaml.cs` 优先使用注入值。
17. **快速输入与导航重入**：正文按键、分页点击和分页滚轮共享 `TurnReaderPageAsync` 的单消费者；待处理状态是有界的“最新一次导航”，消费者释放 gate 后以循环处理收尾竞态，禁止递归重新进入。滚动模式左右键和窗口焦点回退也必须走该入口。关闭/重新打开阅读器时清空 pending；跨文档加载仍由 session/navigation cancellation token 终止旧请求。滚动边缘必须通过 `getContinuousScrollMetrics` 同时比较 `window`、`html`、`body` 的最大内容尺寸，不能退回仅使用 `document.scrollingElement`，否则长章节开头会被误判为底部并直接跳章。

## 5. 主要数据表

```text
ReaderProgress       按 BookFileId 一行：章节路径/fragment/章节索引/滚动位置/进度百分比/FlowMode
ReaderBookmarks      书签：章节标题 + 引文 + 创建时间，点击跳转
ReaderLayoutSettings 按 BookFileId 一行：字号/行高/正文宽度/边距/字体/FlowMode/竖排/TwoPageMode
ReaderReadingStats   按 BookFileId 一行：累计阅读秒数/进度/已读章节/总章节
ReaderAnnotations    划线/批注：章节路径、片段、起止偏移、锚点、颜色、笔记
BookContentChunks    正文索引（FTS trigram，SourceHash 判重建，异常回退 LIKE）
DeviceModels         Serial(Identity) 主键：用户自定义设备型号（UPSERT 覆盖）
AppSettings          应用设置；API Key 存 ai-settings.json（DPAPI / Secret Service / Keychain 加密）
```

## 6. 构建、测试与发布

构建与测试（需要 .NET 8 SDK；本机 8.0.422 / 9.0.315 均可）：

默认开发构建（除非用户明确要求发布）：

```powershell
dotnet build Kkindle.sln -c Debug -p:Platform=x64
```

完整测试构建（Windows，240 项 = 206 可移植 + 28 Windows + 6 平台公共）：

```powershell
dotnet build Kkindle.sln -c Release -p:Platform=x64
dotnet test  Kkindle.sln -c Release -p:Platform=x64 --no-build
```

用户要求单文件调试 EXE 时：

```powershell
dotnet publish src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj `
  -c Debug -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o artifacts\Kkindle-debug-win-x64-current --no-restore
Copy-Item artifacts\Kkindle-debug-win-x64-current\Kkindle.exe `
  artifacts\Kkindle-debug.exe -Force
```

同步固定 Debug EXE 时只覆盖 `artifacts\Kkindle-debug.exe`，保留 `artifacts\data\`、`backups\` 和崩溃日志。

三端独立构建与测试：

```sh
dotnet build src/Kkindle.Desktop.Windows/Kkindle.Desktop.Windows.csproj -c Debug -p:Platform=x64
dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj -c Release
dotnet build src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj -c Release
dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj -c Release   # 任意平台
dotnet test tests/Kkindle.Platform.Common.Tests/Kkindle.Platform.Common.Tests.csproj -c Release
```

Windows 发布（不捆绑 Calibre；生成便携包 + 安装包 + SHA256SUMS）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 `
  -Version 0.5.2 `
  -OutputRoot 'C:\Users\kings\Desktop\01_Projects\Kkindle\artifacts\Kkindle-<Version>'
```

- `-Version` 必须匹配 `^\d+\.\d+\.\d+(-后缀)?$`；`-OutputRoot` 必须是不存在的全新目录；`-SkipInstaller` 跳过安装包。脚本已移除 `-CalibreRuntime` 参数。
- Linux/macOS 发布：

```sh
bash scripts/build-linux-release.sh 0.5.2 artifacts/linux linux-x64   # 也支持 linux-arm64
bash scripts/build-macos-release.sh 0.5.2 artifacts/macos osx-arm64  # 也支持 osx-x64
```

- Linux 产物：`kkindle_<version>_amd64.deb` / `_arm64.deb`（依赖 `libwebkit2gtk-4.1-0`、`libsecret-tools`，推荐 `udisks2`，建议 `calibre`/`libwpewebkit-2.0-1`）+ `Kkindle-<version>-linux-<rid>.tar.gz`。macOS 产物：`Kkindle-<version>-osx-<rid>.tar.gz`（内含 `Kkindle.app`，默认 ad-hoc 签名；设 `APPLE_SIGNING_IDENTITY` 走 Developer ID 硬运行时签名，设 `APPLE_NOTARY_PROFILE` 走 notarytool 公证）。
- 三端发布包均不得复制或下载 Calibre/KFX Input；运行时自动发现系统 Calibre，或由用户在设置中指定/安装。
- Windows 安装包（Inno Setup 6）：默认装到 `%LOCALAPPDATA%\Programs\Kkindle`，卸载不删 `data`/`backups`/`app-root.json`；未配置代码签名，SmartScreen 可能提示未知发布者。
- GitHub：推送 `vX.Y.Z` 标签触发 `.github\workflows\release.yml`：`windows`（windows-2022）、`linux`（ubuntu-24.04，amd64+arm64）、`macos`（macos-15，arm64+x64）三 job 并行构建，`github-release` job 汇总三端产物计算 `SHA256SUMS.txt` 并创建/更新 Release。`.github\workflows\cross-platform.yml` 在 PR / 手动触发时对 Linux、macOS 执行构建 + 可移植与平台公共测试 + 打包 + `dpkg-deb`/`codesign` 校验。

## 7. 已知限制与待人工验收

- 分页模式竖排不生效（有提示），竖排仅滚动模式。
- PDF 使用 WebView 内置查看器渲染：无 Kreader 缩放控制、无页内搜索（Ctrl+F 走全书搜索）、无页内划线显示（标注在笔记列表并可跳页）、无左右区域点击翻页（键盘/按钮/滑块/滚轮可用）；PDF 文本选择无选区工具条。以上基于 Windows WebView2 行为；Linux/macOS WebKit 引擎下 PDF 表现需真机复核。
- Windows 左右滑动/水波动画通过 GDI 捕获 WebView2 当前可见页，再由脚本覆盖层或 View Transition 合成；截图失败、窗口被遮挡或非 Windows 平台会回退淡入。水波是新 Kindle 灰度刷新效果的近似复刻，不是电子墨水屏物理波形仿真。
- WebView2 安全基线：无法恢复旧版 `IsScriptEnabled=false`（桥接依赖 `InvokeScript`），已由净化（去 script/iframe/on* 属性）+ CSP nonce + `contextmenu` 页面级取消 + `EnableDevTools=false` 兜底；Chromium 默认右键菜单已被页面事件取消。
- 波浪/滑动/淡入动画观感、PDF 真实书籍（扫描页/图片/公式）、禅模式鼠标唤醒、AI Markdown 气泡、标注锚定定位等需在交互桌面人工复核。
- **Linux/macOS 待验收**：真实桌面会话（窗口 chrome、字体渲染、缩放）、真实挂载 Kindle（扫描/发送/删除/弹出、`My Clippings.txt`）、无密钥环环境（`secret-tool`/Keychain 缺失时的行为）、WebKit 阅读器（分页数学、脚注、AI 气泡）均未在真机执行；`cross-platform.yml` 只会在 PR/手动触发时跑，尚未实跑。
- MTP-only Kindle（无 U 盘挂载）在 Linux/macOS 暂不支持；Windows 保留完整 WPD/MTP 支持。
- macOS 公开分发仍需 Developer ID 凭据 + 公证；ad-hoc 签名不满足 Gatekeeper。
- 真实 Kindle 物理拔出/重连事件、USB 磁盘型 Kindle 的安全弹出未自动化验收。
- 安装包未配置代码签名（Windows）。

## 8. 不要做的事情

- 除非用户明确要求，不要生成 Release EXE、安装包或便携包；默认只生成保留完整调试工具与运行依赖的 x64 Debug EXE。
- 除非用户明确要求，不要偏离黑/白/灰三色界面、直角矩形按钮和黑白开关的视觉基线。
- 不要读取/修改 calibre 数据库；不要访问 Kindle `system` 目录或内部数据库（缩略图写入除外）；不处理/破解 DRM。
- 不要覆盖 Kindle 上内容不同的同名文件。
- 不要因修 UI 而改变 SQLite 结构却不更新迁移（新表 `IF NOT EXISTS`；旧表 `ALTER` 补列）。
- 不要把单个导入/转换失败升级为整批失败。
- 不要启用 EPUB 页面脚本或扩大 WebView2 导航白名单。
- 不要改动 `App.axaml` 资源合并层级；不要重做标题栏架构或改变窗口 chrome 初始化顺序。
- 不要给 fragment 断点类混入 `page-break-before`。
- 不要恢复已删除的临时诊断文件或旧纸质翻页实现。
- 不要恢复发布包捆绑 Calibre/KFX Input 的逻辑（Calibre 由用户安装或经 `CalibreSetupService` 手动安装）。

## 9. Git 状态与约定

- 2026-08-17 的 `master` 已包含阶段 7 跨平台提交；本次提交继续合入 Kreader 正文渲染、前后翻页、可见页书签、脚注 Popup、禅模式原生控件、页面快照动画、WebView2 状态栏关闭、跨平台 CI/发布更新与相关测试。
- 仓库根目录可能保留本地截图、打印窗口诊断脚本和 `debug-senddiag/` 等未跟踪调试材料；这些不是产品源码，提交时必须显式列出已跟踪文件，禁止 `git add -A` 把它们带入仓库。
- 约定：一次 exe 发布对应一次 Git 提交；每次代码/文档改动随 AI_HANDOFF 一并提交；继续工作前先 `git status --short --branch`。
- GitHub：`git@github.com:kingstacker/Kkindle.git`。
- 另有独立工作树分支 `codex/cross-platform`（`C:\Users\kings\Desktop\01_Projects\Kkindle-crossplatform`，指向 `bf578fe`），与本仓库 master 历史同源，仅供对照，勿混用。

## 10. 跨平台迁移计划（Avalonia）

> 状态：**阶段 0、1、2、3 已完成，阶段 4 已完成（核心交互切片 + 像素级布局重构 + 功能对等缺口全量移植），阶段 5 发布入口已完成，阶段 6 Windows 侧验证已完成，阶段 7 三端落地已完成并提交**。目标是把 UI 层从 WinUI 3 换成 Avalonia，先在 Windows 上达到功能对等，同时把平台相关代码隔离干净，使后续接 Linux/Mac 只需新增平台实现、不改业务代码。
>
> 阶段 7 之后：Linux/macOS 已具备可构建、可打包、可运行的启动头与平台层，但**尚未完成真机/真实桌面验收**，仍不视为三端正式交付版本。

### 10.1 为什么必须换

WinUI 3 / Windows App SDK 没有 Linux/Mac 实现，UI 层无法移植。此外阅读器依赖 WebView2、Kindle 访问依赖 WPD/MTP COM、密钥依赖 DPAPI，都是 Windows 独有。阶段 7 已用跨平台替代实现把这三项在 Linux/macOS 上补齐（挂载 U 盘 Kindle、Secret Service/Keychain、WebKit 阅读器）。

### 10.2 现状盘点（2026-08-15 估算，含 .cs/.xaml/.axaml 行数）

| 项目 | 规模（约） | 说明 |
|---|---|---|
| `Kkindle.Core` | 1,100 行，`net8.0` | 不动。`Services.cs` 接口已平台无关 |
| `Kkindle.Infrastructure` | 7,600 行，`net8.0` | Windows 代码已在阶段 0 外移；新增 Calibre 定位/安装服务 |
| `Kkindle.Platform.Common` | 650 行，`net8.0` | 新增：挂载 U 盘 Kindle 设备服务 + AES-GCM 密钥保护基类 |
| `Kkindle.Platform.Windows` | `net8.0-windows` | WPD/MTP、DPAPI、WM_DEVICECHANGE |
| `Kkindle.Platform.Linux` | 140 行，`net8.0` | 新增：Secret Service、XDG 路径、udisksctl 弹出 |
| `Kkindle.Platform.MacOS` | 90 行，`net8.0` | 新增：Keychain、Application Support 路径、diskutil 弹出 |
| `Kkindle.App` | 18,900 行 C#+AXAML，`net8.0` | Avalonia 界面（含阅读器宿主与注入脚本） |
| `Kkindle.App.WinUI` | 23,600 行 C#+XAML，`net8.0-windows` | WinUI 参考版，对等验收后删除 |
| `Kkindle.Tests` | 3,000+ 行，206 项 | 可移植测试 |
| `Kkindle.Platform.Common.Tests` | 130 行，6 项 | 新增：AES-GCM 往返/防篡改、定位/路径守卫 |
| `Kkindle.Tests.Windows` | 640 行，28 项 | WPD/MTP 设备测试 |

Infrastructure 已无 Windows 专属依赖；平台能力全部落在 `Kkindle.Platform.*`。

### 10.3 目标结构

```text
src/
  Kkindle.Core/                net8.0          不变 + 平台抽象接口
  Kkindle.Infrastructure/      net8.0          SQLite、Calibre 定位/安装、AI、备份、Z-Library
  Kkindle.Platform.Common/     net8.0          挂载 U 盘 Kindle 设备服务、AES-GCM 密钥基类
  Kkindle.Platform.Windows/    net8.0-windows  WPD/MTP、DPAPI、WM_DEVICECHANGE
  Kkindle.Platform.Linux/      net8.0          Secret Service、XDG、udisksctl
  Kkindle.Platform.MacOS/      net8.0          Keychain、Application Support、diskutil
  Kkindle.App/                 net8.0          Avalonia UI（库，全部界面代码）
  Kkindle.Desktop.Windows/     net8.0-windows  WinExe 启动头，只做 DI 装配
  Kkindle.Desktop.Linux/       net8.0          Linux 启动头
  Kkindle.Desktop.MacOS/       net8.0          macOS 启动头
  Kkindle.App.WinUI/           net8.0-windows  ← 迁移期参照，对等后删除
tests/Kkindle.Tests/           net8.0          可移植测试
tests/Kkindle.Platform.Common.Tests/ net8.0    平台公共层测试
tests/Kkindle.Tests.Windows/   net8.0-windows  设备测试（WPD/MTP）
```

**阶段 7 之后该结构已落地**（`Kkindle.sln` 已登记全部项目）；Linux/macOS 头从 `AppServices` 注入 `MassStorageKindleDeviceService`（挂载 U 盘）与平台密钥保护器，`CreateDeviceChangeNotifier` 返回 `null`（UI 回退轮询）。

**为什么要有启动头**：`net8.0` 项目无法引用 `net8.0-windows` 项目（SDK 硬性限制），所以 UI 拿不到平台实现。解法是每平台一个瘦启动头：它持有 `Main`，挑选本平台实现，通过 `AppServices` record 交给可移植的 `Kkindle.App`。阶段 7 验证：编译器会强制 UI 保持可移植——任何 Windows API 混进 `Kkindle.App` 会立即编译失败。

`AppServices.CreateDeviceChangeNotifier` 是 `Func<IntPtr, IDeviceChangeNotifier?>` 工厂而非实例：Windows 版要子类化窗口过程，必须等窗口创建后拿到句柄；Linux/macOS 传 `_ => null`，调用方回退到轮询。

新增 `Kkindle.Core/PlatformServices.cs` 定义三个接口（`IKindleDeviceService` 已存在，直接复用）：`ISecretProtector`（Windows DPAPI / Linux Secret Service / macOS Keychain）、`IDeviceChangeNotifier`（WM_DEVICECHANGE 子类化）、`IReaderHost`（阅读器 WebView 宿主抽象，阶段 4 已定义）。

### 10.4 三个关键决策

**A. MainWindow 保持单体，先求 1:1 对等。** code-behind 大量直接引用 XAML 命名元素，Avalonia 版沿用同名 `MainWindow.axaml` + 同名分部文件 + 同名元素，让迁移尽量机械化。拆成 UserControl/页面留到迁移完成后作为独立重构。

**B. XAML 必须重写，不能机械转换。** Avalonia 样式是 CSS 式选择器，与 WinUI `Style TargetType` + `VisualStateManager` 不同；`App.axaml` 的黑白灰设计系统已整体重建为 Avalonia `Styles`/`ControlTheme`（阶段 1 完成）。

**C. 阅读器：把「钩子 + 轮询」反转为标准 JS 桥（已完成，阶段 4）。** `EpubReaderPreparationService` 对 HTML 落盘消毒后打开脚本开关，用 `postMessage` / `WebMessageReceived` 双向通信；全局钩子与轮询已删除，点击分区/选区/滚动接章/脚注悬停变成真实 DOM 事件（桥接消息见 `ReaderBridgeScript`，含 `wheel`/`key`/`pointermove`/`selectionchange`/`contextmenu` 等通道）。

**WebView 引擎选择（阶段 4 已定，阶段 7 兑现三端）**：使用官方 `Avalonia.Controls.WebView` 12.0.1，藏在 `IReaderHost` 后：Windows 用 WebView2，Linux 用 WebKitGTK 4.1（有 WPE WebKit 2.0 时优先 WPE），macOS 用系统 WebView。渲染差异与字体注入需按平台复核（约束 #15）。

### 10.5 实施阶段

**阶段 0：结构重整（WinUI 版保持可用）— 已完成**

1. 新建 `src/Kkindle.Platform.Windows`，移入 `WpdKindleAccess.cs`、`WpdSessionCloser.cs`、`ShellFileOperation.cs`、`KindleDeviceService.cs`
2. 从 `AiServices.cs` 抽出 `WindowsDataProtection` → `WindowsSecretProtector : ISecretProtector`；`AiServices` / `KindleEmailServices` / `ZLibraryService` 三处改构造注入
3. 新增 `Kkindle.Core/PlatformServices.cs`
4. `Kkindle.Infrastructure` 与 `Kkindle.Tests` TFM 降为 `net8.0`
5. 现 `Kkindle.App` 改名 `Kkindle.App.WinUI`，引用新平台层

进度（勾选项已编译并通过测试）：

- [x] 新建 `src/Kkindle.Platform.Windows`，加入 `Kkindle.sln`
- [x] `Kkindle.Core/PlatformServices.cs`：`ISecretProtector`、`IDeviceChangeNotifier`
- [x] `WindowsDataProtection` → `Platform.Windows/WindowsSecretProtector.cs`；四处改构造注入
- [x] `NativeDeviceChangeMonitor` → `Platform.Windows/WindowsDeviceChangeNotifier.cs`
- [x] 移入 WPD / shell32 / `KindleDeviceService`；Infrastructure 已无任何 Windows API
- [x] Infrastructure 降 TFM 到 `net8.0`（编译零警告，无 CA1416 平台兼容性问题）
- [x] 拆出 `tests/Kkindle.Tests.Windows` 承接 `KindleDeviceTests.cs`，`Kkindle.Tests` 降 TFM
- [x] `Kkindle.App` 改名 `Kkindle.App.WinUI`（迁移期参照工程保留）

阶段 0 完成。四个提交：`1b854e6`、`6792e58`、`6df5734`、`3a502cf`。

`KindleBookClassifier` 与 `KindleScanCacheStore` 保持 `internal`，靠 `Kkindle.Infrastructure.csproj` 里的 `<InternalsVisibleTo Include="Kkindle.Platform.Windows" />` 跨程序集访问；阶段 7 追加了 `Kkindle.Platform.Common` 与 `Kkindle.Tests`。

**DPAPI blob 必须字节兼容**：`WindowsSecretProtector` 的 P/Invoke 逐字搬移，包括描述串 `"Kkindle AI API Key"`（DPAPI 把描述当元数据，不参与解密）。动这块会让老用户升级后 API Key、SMTP 密码、Z-Library 登录静默失效。

测试用 `TestHelpers.PlaintextSecretProtector` 替身，不碰系统密钥库。验收：测试全通过；WinUI 版仍能启动。**此阶段是纯搬移，不改任何业务逻辑。**

**阶段 1：Avalonia 骨架与设计系统 — 已完成**

环境事实（2026-08-13 实测）：Avalonia **12.1.1**、`FluentAvaloniaUI` **3.0.2**、`Avalonia.Controls.WebView` **12.0.1**（owner 是 `avaloniaui`）。模板默认 TFM `net10.0` 本机 SDK 不支持，需手改为 `net8.0`。

- [x] 建 `Kkindle.App`（Avalonia 库）+ `Kkindle.Desktop.Windows`（启动头）
- [x] 自绘方角标题栏（`WindowDecorations=None` + `ExtendClientAreaToDecorationsHint`）
- [x] 自定义 `ScrollBar` ControlTheme（上下三角、可拖动滑块、透明滑轨、内建自动隐藏）
- [x] `App.axaml` 黑白灰基础设计系统
- [x] 内置京华老宋体走 `avares://` 资源

阶段 1 骨架验收（2026-08-13）：solution Debug 构建 0 警告/0 错误；测试 191 项全通过；Windows Avalonia 启动保持运行。

**阶段 2：本地书库 — 已完成（2026-08-13）**

书架/列表/收藏夹三视图、搜索与筛选、详情元数据编辑、动态右键菜单、Ctrl 多选批量操作、格式打开/删除、Calibre 格式转换入口、豆瓣元数据匹配、收藏夹管理、Avalonia `StorageProvider` 文件选择器；WinUI 版本保持不变；内置 Kreader 留给阶段 4。验收：构建 0 警告/0 错误；测试 191/191；启动保持运行。

**阶段 3：Kindle 设备 / 阅读资料 / Z-Library / 设置 / 备份 — 已完成（2026-08-14）**

- [x] Windows 启动头注入 `IKindleDeviceService` 与 `ISecretProtector`
- [x] Kindle 书库页、字体/字典页、阅读资料页、Z-Library 页、设置与备份页
- [x] 修复 Avalonia XAML 初始化期间事件提前触发导致的启动崩溃

验收（2026-08-14）：构建 0 警告/0 错误；测试 191/191；启动保持运行 8 秒。外部依赖闭环（真机/真实 Z-Library/SMTP）待人工验收。

**阶段 4：Kreader 阅读器 — 已完成（2026-08-14）**

- [x] `Avalonia.Controls.WebView` + `IReaderHost`/`NativeWebViewReaderHost`，双 WebView 预加载
- [x] EPUB 落盘消毒 + CSP nonce + `ready` 桥注入；导航白名单限缓存根目录
- [x] 章节切换/进度恢复/基础进度写回；TOC/fragment、滚动/分页、排版、搜索、书签、划线、统计、禅模式、`postMessage` 桥
- [x] 关闭路径先保存再取消宿主；分页横向滚动位置恢复
- [x] AI 助手、脚注点击/悬停预览、PDF 文字阅读表面、四档翻页过渡、阅读模式、分页分区点击、选区工具栏
- [x] 工具栏对齐旧版、极简目录 rail、书签角标、阅读统计、F11/Esc 禅模式、滚动接章、全书搜索计数与高亮、AI 思考深度中文标签、空心标签视觉
- [x] 阅读器 XAML 三栏像素级重构（TOC 286 / 正文 52+50 / 助手 360）、TOC 6 行面板、标题栏品牌与 zen 按钮、过渡遮罩、Avalonia 12 兼容修复
- [x] **功能对等缺口全量移植（22 项，见 `docs/Kreader功能对比.md`）**：PDF 徽标、分页滚轮翻页、滚动模式键盘、禅模式鼠标唤醒、键盘选区工具条、划线样式快速选择器、contextmenu 取消、PDF 真实渲染、AI 气泡 Markdown（`KreaderMarkdownTextBlock`）、正文外观覆盖 CSS、字体项与回退栈、标注重定位锚定、词典弹窗、复制清选区、Esc 层叠关闭、按钮 ToolTip、滑块拖动提示、短章节连跳、导航意图管线、波浪翻页增强、代码卫生
- [x] 阶段 4 核心切片回归后 Debug x64 测试 192 项全通过（2026-08-17 增至 240 项）

遗留架构差异（有意保留，见第 7 节与 `docs/Kreader功能对比.md`）：PDF 点击左右区域翻页在 WebView 内置查看器下不存在；`IsScriptEnabled=false` 安全基线无法恢复（净化 + CSP nonce 兜底）。

**阶段 5：打包发布 — 已完成**

`scripts\Build-Release.ps1` 发布 `src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj`，去掉 `WindowsAppSDKSelfContained`；三端发布包统一不捆绑 Calibre/KFX Input，应用自动发现系统安装或读取用户指定的 `ebook-convert`。Release workflow 增加 Avalonia Windows head 的显式构建。`Kkindle.App.WinUI` 暂不删除，待对等和人工验收后作为独立提交移除。

**阶段 6：扩展性验证 — 已完成（Windows 侧）**

`Kkindle.Core`、`Kkindle.Infrastructure`、`Kkindle.App` 均保持 `net8.0`，可移植层没有发现 Windows UI/WinRT/COM/PInvoke 引用；Debug/Release solution build 与可移植测试全部通过。当时 WSL 无 distribution，无法本机跑 Linux 验证；阶段 7 已改用 GitHub Actions 的 ubuntu/macos runner 覆盖。

**阶段 7：Linux/macOS 三端落地 — 已完成并提交（2026-08-15）**

- [x] `Kkindle.Platform.Common`：`MassStorageKindleDeviceService : IKindleDeviceService`（检测/扫描/发送/删除/导出/资源/剪贴/弹出，路径与链接白名单），`AesGcmSecretProtector` 抽象基类（`KKS1` 头 + 随机 nonce + AAD）。
- [x] `Kkindle.Platform.Linux`：`LinuxSecretProtector`（`secret-tool` lookup/store）、`LinuxAppData`（XDG 数据/配置目录）、`LinuxKindleEjector`（`findmnt` + `udisksctl unmount`，回退 `umount`）。
- [x] `Kkindle.Platform.MacOS`：`MacOSSecretProtector`（`security` 登录钥匙串）、`MacOSAppData`（`~/Library/Application Support/Kkindle`）、`MacOSKindleEjector`（`diskutil eject`）。
- [x] `Kkindle.Desktop.Linux` / `Kkindle.Desktop.MacOS` 启动头：`AppServices` 装配（平台密钥保护器 + `MassStorageKindleDeviceService` + `Paths`/`RootConfigurationDirectory` 注入），`net8.0` 可跨平台编译。
- [x] `AppServices` 扩展 `Paths` 与 `RootConfigurationDirectory`；`App.axaml.cs` 优先使用注入值；`AppRootConfiguration.ResolveRoot(configurationDirectory, fallbackRoot)` 配置/数据目录分离（含测试）。
- [x] Calibre 三端发现（`CalibreExecutableLocator`，平台候选路径 + 覆盖变量 + PATH，无 `.exe` 后缀假设）与用户手动安装（`CalibreSetupService`：Windows MSI / Linux 隔离安装器 / macOS DMG 校验；KFX Input 插件下载校验安装；仅自装插件时隔离 `CALIBRE_CONFIG_DIRECTORY`）；设置页新增安装按钮与进度条。
- [x] 阅读器 Linux 引擎适配：`NativeWebViewReaderHost` 探测 `libWPEWebKit-2.0.so.1`，缺失则 `PreferWebKitGtkInstead` 切 WebKitGTK 4.1。
- [x] 三端发布脚本（`build-linux-release.sh`：.deb + tar.gz，依赖声明；`build-macos-release.sh`：.app + Info.plist + ad-hoc/Developer ID 签名 + 可选公证）+ `release.yml` 三 job 并行 + 聚合 GitHub Release + `cross-platform.yml` PR 校验（构建/测试/打包/`dpkg-deb`/`codesign`）+ `docs/cross-platform.md`。
- [x] 阅读器收尾：EPUB 远程脚注图消毒（无源 `<img>` 替换为 `<sup class="kkindle-footnote-marker">` 或删除，`ExtractionFormatVersion` 8）、极简目录 marker 改 `RenderTransform` 平移（修复悬停波峰错位）、全书搜索结果条目新样式、筛选面板移除过渡动画（修复缩放闪烁）。
- [x] **窗口关闭默认先退书架**：Avalonia `MainWindow_Closing`（取消关闭 + `CloseReaderAsync`）与 `CloseWindowButton_Click`；WinUI 参照版同步 `AppWindow_Closing` 与 `CloseWindowButton_Click`；阅读中关闭窗口只退主界面，主界面再次关闭才退出。
- [x] 测试基线已在 2026-08-17 增至：`Kkindle.Tests` 206 + `Kkindle.Platform.Common.Tests` 6 + `Kkindle.Tests.Windows` 28 = 240 项 Debug。

阶段 7 待办（未完成）：三端 CI 实跑验收、真实 Linux/macOS 桌面会话与真实 Kindle 验收、macOS 公证凭据配置、像素级截图验收。

**粗略总量：约 2-2.5 个月（阶段 4 占一半且不确定性最高）；阶段 7 为额外增量，尚未验收。**

### 10.6 WinUI → Avalonia 主要映射

| WinUI 3 | Avalonia | 说明 |
|---|---|---|
| `x:Bind`（166 处） | `{CompiledBinding}` + `x:DataType` | 逐处改写，不能机械替换 |
| `ListView` / `GridView` | `ListBox` + `ItemsPanelTemplate`(`WrapPanel`) | GridView 无直接对应 |
| `FontIcon` / `SymbolIcon` / `NumberBox` / `ContentDialog` | FluentAvalonia | Avalonia 本体没有 |
| `Style` + `VisualStateManager` | `Style Selector` + 伪类（`:pointerover` / `:pressed`） | `App.axaml` 需整体重建 |
| `DispatcherQueue` | `Dispatcher.UIThread` | |
| `AppWindow` / `OverlappedPresenter` / `DwmSetWindowAttribute` | `Window` + `ExtendClientAreaToDecorationsHint` / `WindowState` | 跨平台 |
| `FileOpenPicker` + `InitializeWithWindow` | `StorageProvider` | 跨平台 |
| `WebView2` 控件 | `Avalonia.Controls.WebView`（Win/WebView2、Linux/WebKitGTK·WPE、macOS/系统 WebView） | 藏在 `IReaderHost` 后 |
| `SetWindowsHookEx` + 轮询 | `postMessage` JS 桥 | 阶段 4 已完成 |
| `WindowsDataProtection`（DPAPI） | `ISecretProtector`（DPAPI / Secret Service / Keychain） | 平台层注入 |
| `AppPaths(AppContext.BaseDirectory)` | `AppRootConfiguration.ResolveRoot(配置目录, 数据目录)` | XDG / Application Support |
| `KindleDeviceService`（WPD/MTP） | `MassStorageKindleDeviceService`（挂载 U 盘） | Linux/macOS；Windows 保留 WPD |

### 10.7 验证方式

- 每阶段：`dotnet build Kkindle.sln -c Debug -p:Platform=x64`（约定 #12：默认只出 x64 Debug）+ 启动运行
- 回归基线：**240 项测试**必须始终全绿（206 可移植 + 28 Windows + 6 平台公共）
- 三端校验：`.github/workflows/cross-platform.yml`（PR/手动）在 ubuntu-24.04 / macos-15 上构建启动头、跑可移植与平台公共测试、打包并做 `dpkg-deb`/`codesign` 检查
- 视觉对照：`docs\images\` 下 9 张截图即验收基线，逐屏对照
- 阶段 4 人工验收（无法自动化，见第 7 节）：分页分区点击、滚动接章、选区工具栏、四种翻页动画观感、禅模式进出；AI/脚注/PDF 需要在对等切片后补验
- 真机验收：Windows Kindle Scribe（MTP）发送/扫描/删除闭环、USB 磁盘型安全弹出；Linux/macOS 挂载 U 盘 Kindle 闭环待补
- 阶段 6：Windows 可移植层扫描与 Debug/Release 回归已通过；阶段 7 改用 GitHub Actions 覆盖 Linux/macOS 构建

### 10.8 当前 UI / 功能对等审计（2026-08-15）

当前结论：功能对等缺口已按 `docs/Kreader功能对比.md` 全量移植，三端平台层与启动头已提交，但**尚未完成**同分辨率/DPI 像素级截图与真实书籍交互验收，Linux/macOS 尚未真机验收，不得将当前 Debug EXE 标记为 WinUI 旧版的 1:1 对等版本。

- [x] 已完成旧版 `src\Kkindle.App.WinUI` 与当前 `src\Kkindle.App` 主窗口 XAML 的静态入口盘点：旧版约 204 个 XAML 事件入口，当前约 147 个，约 57 个旧入口没有在当前主窗口 XAML 中一一对应。由于 Avalonia 可能改用不同事件或代码绑定，这个数字是差异证据，不直接等同于缺失功能数量。
- [ ] 建立旧版所有页面、子项、右键菜单、拖拽、批量操作、设备操作、格式转换和阅读器工具的功能矩阵，并逐项在 Avalonia 中实测闭环。
- [ ] 使用相同窗口尺寸、DPI、字体和数据集，对 `docs\images\` 基线截图逐屏截图并进行像素差异验收；当前只做过局部样式常量对照，没有完成像素级验收。
- [x] 已补齐 AI 助手、脚注浮窗、PDF 阅读表面、四档翻页过渡、阅读模式/排版设置、分页分区点击和选区工具栏的 Avalonia 功能闭环；四档动画与旧版的视觉实现仍不是像素级等价。
- [x] 已按 `docs/Kreader功能对比.md` 全量移植 22 项缺口（P0 PDF 徽标、P1 输入层六项、P2 渲染差异四项、P3 行为差异九项、架构两项的可行部分），详见 10.5「阶段 4 功能对等缺口全量移植」；遗留限制记录于第 7 节。
- [x] 2026-08-17 已重新构建并运行 240 项测试（206 + 28 + 6）；Windows Avalonia 单文件 Debug EXE 已由用户确认打开书籍不再闪退。
- [x] 已落地并提交三端平台层/启动头/发布体系与 Calibre 去捆绑（见 10.5 阶段 7）；**窗口关闭默认先退书架**已在 Avalonia 与 WinUI 参照版同步实现。
- [ ] 仍需用真实 EPUB/PDF、真实 Kindle（Windows + Linux/macOS）、相同 DPI 数据集完成手工功能矩阵和截图验收，之后才能把版本标记为 1:1 对等。

### 10.9 风险

1. **分页 CSS 数学**（约束 #2）：列宽 + 间距必须严格等于视口宽，换渲染宿主后 DPI 缩放与 `clientWidth` 取值可能有差异，最可能出现「差一像素、翻页错位」。Linux/macOS 走 WebKit 引擎，需真机复核。
2. **JS 桥改造引入阅读器回归**：现有导航守卫（`_readerChapterTransitionSequence` + 取消令牌）是为轮询模型设计的，改事件驱动后时序会变，约束 #4/#5 需重新推演而不是照搬。
3. **FluentAvalonia 定制上限**：`ContentDialog` 等控件的黑白灰定制程度需在阶段 1 就验证（已通过骨架验证）。
4. **迁移期双份代码**：`Kkindle.App.WinUI` 保留期间，Infrastructure 的改动要同时满足两边。建议迁移期冻结新功能开发。
5. ~~**硬编码可执行文件路径**~~ **已解决（阶段 7）**：`BookFormatConversionService.cs` 的硬编码 `.exe` 路径已替换为 `CalibreExecutableLocator`（按 `DesktopOperatingSystem` 分支、无 `.exe` 后缀假设）。数据目录与设备挂载点假设也已按平台处理（XDG / Application Support / `/media`、`/run/media`、`/Volumes`）。
6. **Linux/macOS 阅读器引擎差异**：WebKitGTK/WPE 与 WebView2 在 PDF 渲染、字体注入、滚轮/选区消息、右键菜单等行为不同，`postMessage` 桥与注入脚本需按平台复核；Linux 上 WPE 缺失时回退 WebKitGTK（`PreferWebKitGtkInstead`）。
7. **真实输入压力仍需人工验收**：快速按键已改为非递归串行消费者并通过构建/单元测试，但 WebView2 的真实按键重复、窗口焦点切换和超长 EPUB 章节加载仍需用户在单页、双页/双栏及滚动模式下持续压力测试。
8. **密钥迁移兼容**：Linux/macOS 的 `AesGcmSecretProtector` 是新格式（`KKS1` 头），与 Windows DPAPI blob 不互通——同一份数据目录跨平台迁移时密钥需重新配置；三端数据目录互不兼容，迁移/备份功能按平台隔离。
