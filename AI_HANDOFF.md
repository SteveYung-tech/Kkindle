# Kkindle 项目交接文档

> 给后续 AI / 开发者使用。继续工作前请先阅读本文档，再查看代码和当前 Git 状态。
>
> 更新时间：2026-08-14
>
> 项目目录：`C:\Users\kings\Desktop\01_Projects\Kkindle`

> 当前迁移状态（2026-08-14）：Avalonia 阶段 2（本地书库）和阶段 3（Kindle 设备、阅读资料、Z-Library、设置与备份）已完成，阶段 4（Kreader 阅读器）已完成核心交互切片，并按旧版 WinUI Kreader 完成工具栏/极简目录/书签角标/阅读统计/滚动接章/禅模式快捷键/AI 标签与搜索计数等界面与功能对齐；阶段 5 发布入口已切到 Avalonia Windows 启动头，阶段 6 已完成 Windows 侧可移植性验证。WinUI 版本仍保留作迁移参照。当前 Avalonia 版尚未完成与 WinUI 旧版的功能逐项 1:1 对照、同分辨率/DPI 像素级 UI 验收；完整 AI/脚注/PDF 功能和人工视觉验收仍待完成，Debug EXE 仅作为迁移调试版。

## 0. 当前状态

- 基线功能（WinUI 参考版）：P0/P1/P2、本地书库、Kreader 阅读器、阅读资料中心、Kindle 设备管理（USB/WPD/MTP）、格式转换、Z-Library、Kindle 邮件、备份/设置、AI 助手、安装包与 GitHub 自动发版均已实现并验证；Avalonia 当前版已完成阶段 2/3 与阶段 4 核心交互切片，但不能据此视为已与 WinUI 全部等价，具体对等状态见第 10.8 节。
- 分支 `master`；`v0.5.2`（提交 `5daf140`）已推送至 `origin/master` 并自动发版，当前本地包含尚未推送的 Avalonia 迁移收尾提交；阶段 3 收尾提交为 `c71af9e`，本次阶段收尾提交为当前 `HEAD`。
- 最新版本：0.5.2（标签 `v0.5.2`）；在 0.5.1 基础上新增全应用滚动条自动隐藏，滚动或悬停时显示，空闲后淡出；补齐 Popup、折叠面板、ContentDialog 和延迟生成模板的挂载，并隔离嵌套 ScrollViewer 的滚动条归属。
- 测试：Debug x64 192 项全部通过（0 失败、0 跳过，2026-08-14）。阶段 0 拆分后分布在两个项目：`Kkindle.Tests` 164 项（`net8.0`，可跨平台）+ `Kkindle.Tests.Windows` 28 项（`net8.0-windows`，WPD/MTP 设备测试）。
- 当前 Avalonia 调试 EXE：`src\Kkindle.Desktop.Windows\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Kkindle.exe`；已完成构建、192 项测试和启动保持运行检查，但尚未作为 UI/功能 1:1 对等验收包。
- 本地最近完整测试包（2026-08-12 19:36，版本 `0.5.0-test.1`，由 `685ab20` 发布；内置 Calibre 运行时与 KFX Input 插件，启动/关闭验证通过；如需包含 0.5.2 改动请重新发布）：
  - exe：`artifacts\Kkindle-0.5.0-test.1\Kkindle-0.5.0-test.1-win-x64\Kkindle.exe`
  - 便携包：`artifacts\Kkindle-0.5.0-test.1\Kkindle-0.5.0-test.1-win-x64-portable.zip`
- 常规发布目录：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`（其中 exe 仍是 2026-08-10 基线，如需随最新提交刷新请重新发布）。
- 真机验证：Kindle Scribe（MTP）EPUB 发送/扫描/删除闭环、64 MiB 大文件传输、设备字体/字典读写均已验收，设备端无测试残留。
- 开发约定：代码修改必须能编译；每次发布 EXE 只创建一个对应 Git 提交；文档随代码一并提交。
- 已启动 WinUI 3 → Avalonia 迁移（Windows 优先，架构上预留 Linux/Mac），计划与进度见第 10 节；当前处于阶段 4 核心交互切片，WinUI 版继续保留。

## 1. 项目目标与技术路线

Kkindle 是只供个人使用的 Windows 11 Kindle 书库与 USB 管理器：

- 视觉：白底黑字纸张感、零圆角硬边矩形、黑白为主、无渐变/强阴影；全自绘黑白标题栏，DWM 禁用窗口圆角。
- 布局：左侧导航、中间书架、右侧详情；默认中文；本地书库与 Kindle 书库独立入口。
- 技术：C# + .NET 8、WinUI 3 + Windows App SDK 2.3.1、CommunityToolkit.Mvvm 8.4.2、SQLite（Microsoft.Data.Sqlite）；x64、unpackaged、self-contained、便携目录发布。

## 2. 项目结构

```text
Kkindle/
├─ Kkindle.sln / Directory.Build.props
├─ README.md / AI_HANDOFF.md / LICENSE
├─ scripts\Build-Release.ps1     # 统一发布脚本（便携包 + 安装包 + 校验和）
├─ installer\Kkindle.iss         # Inno Setup 6 安装包脚本
├─ .github\workflows\release.yml # 推送 vX.Y.Z 标签自动构建发版
├─ src\Kkindle.App.WinUI/        # WinUI 3 参考实现，作为旧版迁移基线
├─ src\Kkindle.App/              # Avalonia 迁移中的窗口、页面、阅读器宿主与注入脚本
├─ src\Kkindle.Desktop.Windows/  # Avalonia Windows 启动头
├─ src\Kkindle.Core/             # 领域模型、策略（阅读选择/导航/分页/转换/设备型号目录）
├─ src\Kkindle.Infrastructure/   # SQLite、元数据、Kindle 设备、Calibre、AI、备份、Z-Library
└─ tests\Kkindle.Tests/          # 单元测试
```

应用数据目录在 exe 旁：`data\{library,covers,reader-cache,logs,kkindle.db}`、`backups`、`app-root.json`。

## 3. 已实现功能

> 本节是 WinUI 参考版的完整功能基线；Avalonia 迁移版是否已逐项对等，以第 10.8 节的审计结果为准。

### 3.1 本地书库

- 导入文件/文件夹（EPUB/PDF/MOBI/AZW3），复制进 `data/library`，SHA-256 去重、同书多格式合并；单个文件失败不影响整批导入。
- 元数据解析与编辑（标题/作者/系列/标签/简介/封面）；搜索、作者/标签/格式/分类/阅读状态筛选、收藏、排序；书架/列表/网格画廊三种视图。
- 多格式打开子菜单（按 EPUB/PDF/AZW3 优先级）、按格式删除、一键删除全部（二次确认；阅读中先关闭阅读器）。

### 3.2 Kreader 阅读器

- 三栏布局（目录/正文/阅读助手），目录支持完整/极简两种模式；窗口过窄自动收起侧栏。
- 支持 EPUB、PDF（WebView2 内置）、AZW3（自动转临时 EPUB）；`IsScriptEnabled=false` 安全基线，EPUB 自带脚本不运行。
- 滚动/分页/双页/竖排（仅滚动模式）/翻页动画（无、淡入淡出、左右滑动、水波流动）。
- 每书独立排版设置（字号/行高/正文宽度/边距/字体/CJK 覆盖/竖排）持久化；进度断点恢复；书签；书内搜索（FTS + LIKE 回退，带高亮）；划线/批注；脚注悬停浮窗；阅读统计；AI 助手；禅模式。
- 双 WebView 下一章预加载（`ReaderPreloadWebView`）；禅模式为真全屏（FullScreen presenter，F11 进入 / Esc 退出，chrome 自动隐藏）。
- 目录/子章节 fragment 跳转顶格、章节首行归一化、分页列边界吸附（详见第 4 节约束）。
- Kreader 全书搜索结果使用右侧固定槽位中的原生浅灰滚动条，保留上下三角按钮和可拖动滑块；滚动/悬停显示、闲置隐藏，不显示滑轨线。搜索框与结果内容左边距对齐，结果列表仍扩到目录栏边缘以固定原生滚动条位置；每个词条矩形右侧内缩 `14` DIP，与上方搜索框右边界对齐，搜索结果项自身不再显示全局 `ListViewItem` 的额外外框，避免右侧出现多余矩形；滚动条只位于搜索结果区，不影响底部“返回书架”矩形。搜索框文字垂直居中，词条矩形使用更细的 `0.5` DIP 边框。左侧底部按钮铺满整块底栏、与右侧底栏同高，外框采用与右侧底栏一致的 `#E2E2DE` 浅灰线。

### 3.3 阅读资料中心

- 统一汇总本地全部书籍的划线/批注/页级批注 + 已连接 Kindle 的 `My Clippings.txt`；按来源筛选、全文搜索、Markdown/纯文本导出、回到原文定位、逐条删除。
- Kindle 笔记删除仅改写 `My Clippings.txt`，不修改书籍侧车数据库或云端标注。

### 3.4 Kindle 设备

- USB 磁盘 + WPD/MTP 识别；容量、书籍扫描、发送（临时文件 + SHA-256 校验 + 原子改名 + 同名编号）、删除（`IFileOperation`，路径白名单）、安全弹出；`WM_DEVICECHANGE` + 3 秒轮询兜底；设备断开自动取消并精确清理本次文件。
- 设备字体（`fonts` 的 TTF/OTF）与字典（`documents\dictionaries` 的 AZW/AZW3/MOBI/KFX）读取/导入/导出/删除。
- 设备身份用 USB 卷序列号 / WPD shell 路径，盘符变化不影响识别；设备型号记忆（`DeviceModelStore` + `DeviceModelCatalog`，内置 Kindle/汉王/掌阅/Kobo 型号，支持自定义）。

### 3.5 格式转换

- Calibre `ebook-convert`：EPUB/AZW3/PDF 互转，KFX→EPUB（发布包内置 KFX Input 插件）；结果写回原书；实时进度、后台、取消。
- 查找顺序：exe 旁 `Calibre\ebook-convert.exe` → `Calibre2` → 系统安装目录 → PATH → `KKINDLE_CALIBRE_CONVERT`；内置运行时使用独立 `CALIBRE_CONFIG_DIRECTORY`，不污染用户配置。

### 3.6 Z-Library 与 Kindle 邮件

- Z-Library eapi 搜索/下载（格式/语言筛选、分页、自动入库去重、临时文件清理）；账号凭据 DPAPI 加密；API 地址可配置镜像。
- SMTP 发送 EPUB/PDF 到 Kindle 个人文档邮箱；SMTP 密码不写入备份包。

### 3.7 备份、设置与 AI

- `.kkindle` 备份导出/导入/迁移（书库/封面/阅读记录），每日自动备份与保留数量；API Key 与 SMTP 密码不入备份。
- 设置：默认打开格式、默认排版、Calibre 路径、AI/网络权限、数据目录；实时自动保存。
- AI 助手：DeepSeek / OpenAI / 兼容接口，SSE 流式，思考深度与模型选择，本地书库索引检索上下文，选区解释、全书概览、书内问答；对话只发送相关片段，不上传整本书。

## 4. 关键技术约束（必读）

1. **WebView2 安全基线**：`IsScriptEnabled=false`，导航白名单限当前 EPUB 缓存/PDF。该设置下页面事件监听全部不派发，交互一律宿主侧轮询/钩子 + `ExecuteScriptAsync`：滚动接章 150ms 轮询、分页点击低级鼠标钩子、选区 300ms 轮询、脚注悬停轮询。
2. **分页 CSS 数学**：`html { overflow:hidden }` 是唯一滚动容器，`body` 必须 `overflow:visible`；`column-width: calc(100vw - 48px)` + `column-gap: 48px`（列宽+间距必须严格等于视口宽），并显式 `writing-mode: horizontal-tb !important`。翻页/吸附统一用 `scrollingElement.clientWidth` 步进，`SnapReaderPaginationAsync()` 吸附到 `paddingLeft + N×clientWidth`，`top:0` 钉死。
3. **图片/封面**：`img/svg` `max-width:100%`，并用内容盒高度变量 `--kkindle-page-content-h` 做 `max-height` contain 拟合，分页/滚动各自适配，避免裁切或横向溢出。
4. **导航意图与守卫**：`ReaderNavigationIntent`（None/Toc/Progress/Bookmark/Annotation/Search/AiSource）+ `PruneReaderPendingLocations` 只保留本次意图的 pending 位置；导航序列守卫（`_readerChapterTransitionSequence` + 取消令牌 + `_readerCloseRequested`）保证旧导航/旧后置任务不覆盖新章节。普通章节跳转先 `NormalizeReaderChapterStartAsync`（删开头空白节点、首个有效内容元素 margin-top 归零）；fragment 用 `.kkindle-fragment-break`（`break-before: column !important`，**禁止再混入 `page-break-before`**），滚动模式按正文内容盒顶部对齐；带 fragment/书签/批注/搜索/AI 目标不归一化。
5. **章节切换与关闭**：导航期间保持旧内容可见、首屏完成后再短淡入；非首屏任务延迟执行并随时检查守卫；目录/搜索/书签/批注跳转一律短淡入。关闭流程幂等非阻塞：先停钩子/轮询/计时器 → 有界异步落库（不碰 WebView）→ 清理；重复点击 X / 返回书架 / 关窗均安全。
6. **XAML 启动坑**：`Slider`/`ComboBox` 的 `ValueChanged`/`SelectionChanged` 会在 XAML 解析给属性赋值时提前触发，事件处理器必须加 `AreReaderLayoutControlsReady()` 空值守卫，否则启动 `0xC000027B` 崩溃。
7. **窗口 chrome**：`ConfigureTitleBar()` 不得在窗口激活前读 `AppWindow`；`ConfigureNativeWindowChrome()` 只在首次 `Window.Activated` 后调用；`ApplySquareWindowFrame()` 的多次调用时机（首次/低优先级/Loaded/presenter 变化）必须保留，否则 Windows 恢复圆角。主界面左右侧按黄金分割（`ApplyGoldenSidebarWidth`/`ComputeGoldenDetailWidth`，常量在 `MainWindow.xaml.cs` 顶部）。
8. **阅读器脚本模块**：导航/分页/外观/水波分别在 `ReaderNavigationScripts.cs`、`ReaderPaginationScripts.cs`、`ReaderAppearanceScripts.cs`、`ReaderWaveScripts.cs`；动画参数（水波 560ms、32 条带等）只改常量。旧纸质翻页实现（`ReaderFlipCurlScripts`）已删除，不要再恢复。
9. **SQLite**：新表一律 `CREATE TABLE IF NOT EXISTS` 幂等；旧表加列用 `PRAGMA table_info` + `ALTER TABLE`（如 `TwoPageMode`）；`ReaderDataService.InitializeAsync()` 幂等。
10. **内置字体**：京华老宋体 v3.0（33,259,644 字节，SHA-256 `F7FEF9FC413E9E2343F0BB432C51CCA41C44B8FE37F071DC86B050896AE9F9E2`），原生 WinUI 走资源 URI，EPUB 走 `@font-face`。
11. **其他**：`App.xaml` 的 `XamlControlsResources` 合并层级是启动稳定基础，不要改动；源码统一 UTF-8；不要根据终端显示乱码直接判断业务字符串损坏。
12. **默认调试产物**：除非用户明确要求 Release、安装包、便携包或正式发布，否则只生成 x64 Debug EXE；必须保留完整调试工具和运行依赖，不做裁剪或精简，方便调试。
13. **界面视觉限制**：除非用户明确要求，界面严格只使用黑、白、灰三色；按钮使用直角矩形；开关使用黑白样式；不得擅自引入圆角、彩色、渐变或强阴影。

## 5. 主要数据表

```text
ReaderProgress       按 BookFileId 一行：章节路径/fragment/章节索引/滚动位置/进度百分比/FlowMode
ReaderBookmarks      书签：章节标题 + 引文 + 创建时间，点击跳转
ReaderLayoutSettings 按 BookFileId 一行：字号/行高/正文宽度/边距/字体/FlowMode/竖排/TwoPageMode
ReaderReadingStats   按 BookFileId 一行：累计阅读秒数/进度/已读章节/总章节
ReaderAnnotations    划线/批注：章节路径、片段、起止偏移、锚点、颜色、笔记
BookContentChunks    正文索引（FTS trigram，SourceHash 判重建，异常回退 LIKE）
DeviceModels         Serial(Identity) 主键：用户自定义设备型号（UPSERT 覆盖）
AppSettings          应用设置；API Key 存 ai-settings.json（DPAPI 加密）
```

## 6. 构建、测试与发布

构建与测试（需要 .NET 8 SDK；本机 8.0.422 / 9.0.315 均可）：

默认开发构建（除非用户明确要求发布）：

```powershell
dotnet build Kkindle.sln -c Debug -p:Platform=x64
```

完整测试构建：

```powershell
dotnet build Kkindle.sln -c Release -p:Platform=x64
dotnet test  Kkindle.sln -c Release -p:Platform=x64 --no-build
```

完整发布（内置 Calibre 运行时 + KFX Input 插件 + 便携包 + 安装包 + SHA256SUMS）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 `
  -Version 0.5.0-test.1 `
  -CalibreRuntime 'C:\Program Files\Calibre2' `
  -OutputRoot 'C:\Users\kings\Desktop\01_Projects\Kkindle\artifacts\Kkindle-<Version>'
```

- `-Version` 必须匹配 `^\d+\.\d+\.\d+(-后缀)?$`；`-OutputRoot` 必须是不存在的全新目录；`-SkipInstaller` 跳过安装包。
- 提供 `-CalibreRuntime` 时会复制整个 Calibre 目录（1341 个文件，约 626 MB）到 `publish\Calibre`，并下载 `CalibrePlugins\KFX Input.zip` 校验 SHA-256（`6919e8cec65a92f922a14f616eedcb1b9dbb2a79dd4a261f9548e17ca208072f`）。
- 产物：`Kkindle-<Version>-win-x64\Kkindle.exe`、`Kkindle-<Version>-win-x64-portable.zip`、`Kkindle-<Version>-win-x64-setup.exe`（未加 `-SkipInstaller` 时）、`SHA256SUMS.txt`。
- 安装包（Inno Setup 6）：默认装到 `%LOCALAPPDATA%\Programs\Kkindle`，卸载不删 `data`/`backups`/`app-root.json`；未配置代码签名，SmartScreen 可能提示未知发布者。
- GitHub：推送 `vX.Y.Z` 标签触发 `.github\workflows\release.yml` 自动构建并创建 Release。

## 7. 已知限制与待人工验收

- 分页模式竖排不生效（有提示），竖排仅滚动模式。
- WebView2 合成岛的真实鼠标/滚轮端到端（分页分区点击、滚动接章、选区工具栏、翻页动画观感）建议在交互桌面人工复核。
- 真实 Kindle 物理拔出/重连事件、USB 磁盘型 Kindle 的安全弹出未自动化验收。
- 安装包未配置代码签名。

## 8. 不要做的事情

- 除非用户明确要求，不要生成 Release EXE、安装包或便携包；默认只生成保留完整调试工具与运行依赖的 x64 Debug EXE。
- 除非用户明确要求，不要偏离黑/白/灰三色界面、直角矩形按钮和黑白开关的视觉基线。
- 不要读取/修改 calibre 数据库；不要访问 Kindle `system` 目录或内部数据库；不处理/破解 DRM。
- 不要覆盖 Kindle 上内容不同的同名文件。
- 不要因修 UI 而改变 SQLite 结构却不更新迁移（新表 `IF NOT EXISTS`；旧表 `ALTER` 补列）。
- 不要把单个导入/转换失败升级为整批失败。
- 不要启用 EPUB 页面脚本或扩大 WebView2 导航白名单。
- 不要改动 `App.xaml` 资源合并层级；不要重做标题栏架构或改变窗口 chrome 初始化顺序。
- 不要给 fragment 断点类混入 `page-break-before`。
- 不要恢复已删除的临时诊断文件或旧纸质翻页实现。

## 9. Git 状态与约定

- 当前 `master` 工作区干净；本地比 `origin/master` 超前 10 个提交（均未推送）；构建输出由 `.gitignore` 排除。
- 约定：一次 exe 发布对应一次 Git 提交；每次代码/文档改动随 AI_HANDOFF 一并提交；继续工作前先 `git status --short --branch`。
- GitHub：`git@github.com:kingstacker/Kkindle.git`。

## 10. 跨平台迁移计划（Avalonia）

> 状态：**阶段 0、1、2、3 已完成，阶段 4 核心交互切片已完成，阶段 5 发布入口已完成，阶段 6 Windows 侧验证已完成**。目标是把 UI 层从 WinUI 3 换成 Avalonia，先在 Windows 上达到功能对等，同时把平台相关代码隔离干净，使后续接 Linux/Mac 只需新增平台实现、不改业务代码。
>
> 本阶段**不交付** Linux/Mac 可运行版本，只交付「Windows 上的 Avalonia 版 + 已隔离的扩展点」。

### 10.1 为什么必须换

WinUI 3 / Windows App SDK 没有 Linux/Mac 实现，UI 层无法移植。此外阅读器依赖 WebView2、Kindle 访问依赖 WPD/MTP COM、密钥依赖 DPAPI，都是 Windows 独有。

### 10.2 现状盘点（已核实）

| 项目 | 规模 | 迁移影响 |
|---|---|---|
| `Kkindle.Core` | 1,174 行，`net8.0` | 不动。`Services.cs` 接口已平台无关 |
| `Kkindle.Infrastructure` | 9,272 行，`net8.0-windows` | 约 1,500 行 Windows 代码外移，其余原样 |
| `Kkindle.App` | 18,445 行 C# + 6,868 行 XAML | 全部重写（288 处 WinUI 类型引用散布在 33 个文件） |
| `Kkindle.Tests` | 3,828 行，192 项 | 只需降 TFM |

Infrastructure 的 Windows 依赖只集中在 5 处：

- `WpdKindleAccess.cs`（946 行，`Shell.Application` COM）
- `WpdSessionCloser.cs`（`ComImport`）
- `ShellFileOperation.cs`（shell32 `IFileOperation`）
- `AiServices.cs:557-631`（`WindowsDataProtection`，crypt32 DPAPI；被 AI / SMTP / Z-Library 三处共用）
- `KindleDeviceService.cs:813-822`（kernel32 磁盘容量）

XAML 规模：`MainWindow.xaml` 5,894 行含 166 处 `x:Bind`、6 个 `ControlTemplate`；`App.xaml` 974 行含 9 个 `ControlTemplate`。

### 10.3 目标结构

```text
src/
  Kkindle.Core/                net8.0          不变 + 平台抽象接口
  Kkindle.Infrastructure/      net8.0          ← 从 net8.0-windows 降级
  Kkindle.Platform.Windows/    net8.0-windows  WPD/MTP、DPAPI、WM_DEVICECHANGE
  Kkindle.App/                 net8.0          Avalonia UI（库，全部界面代码）
  Kkindle.Desktop.Windows/     net8.0-windows  WinExe 启动头，只做 DI 装配
  Kkindle.App.WinUI/           net8.0-windows  ← 原 Kkindle.App，迁移期参照，对等后删除
tests/Kkindle.Tests/          net8.0          可移植测试，可在任意平台跑
tests/Kkindle.Tests.Windows/  net8.0-windows  设备测试（WPD/MTP，只能在 Windows 跑）
```

**为什么要有启动头**：`net8.0` 项目无法引用 `net8.0-windows` 项目（SDK 硬性限制），所以 UI 拿不到 `WindowsSecretProtector`、`KindleDeviceService`。解法是每平台一个瘦 WinExe：它持有 `Main`，挑选本平台实现，通过 `AppServices` record 交给可移植的 `Kkindle.App`。

真正的收益不是「能编译」，而是**编译器会强制 UI 保持可移植** —— 任何 Windows API 混进 `Kkindle.App` 会立即编译失败，而不是等到跑 Linux 才发现。加 Linux/Mac 时只需新增 `Kkindle.Platform.Linux` + `Kkindle.Desktop.Linux`，UI 一行不动。

`AppServices.CreateDeviceChangeNotifier` 是 `Func<IntPtr, IDeviceChangeNotifier?>` 工厂而非实例：Windows 版要子类化窗口过程，必须等窗口创建后拿到句柄；其他平台可忽略该参数，返回 `null` 表示无通知器、调用方回退到轮询。

新增 `Kkindle.Core/PlatformServices.cs`，定义三个接口（`IKindleDeviceService` 已存在，直接复用）：

- `ISecretProtector` — 替代 `WindowsDataProtection`；Windows 包 DPAPI，Linux 走 libsecret，Mac 走 Keychain
- `IDeviceChangeNotifier` — 替代 `NativeDeviceChangeMonitor` 的 `WM_DEVICECHANGE` 子类化
- `IReaderHost` — 阅读器 WebView 宿主抽象（见 10.4），已在阶段 4 核心交互切片定义

`ShellFileOperation` **不抽接口**：核对后确认它的 8 个调用点全在 `WpdKindleAccess` 内部，操作对象是 WPD shell COM 项而非普通文件路径，属于 WPD 实现细节，随 WPD 一起进 Platform.Windows 即可。

`KindleDeviceService.cs` 本阶段**整体移入 Platform.Windows 不拆分**（USB 磁盘逻辑与 WPD 分发混在一起，边迁 UI 边拆风险过高）。拆出可复用的 USB 磁盘实现留到真正做 Linux 时。

### 10.4 三个关键决策

**A. MainWindow 保持单体，先求 1:1 对等。**
code-behind 大量直接引用 XAML 命名元素（`ReaderActiveWebView`、`RootGrid` 等），Avalonia 版沿用同名 `MainWindow.axaml` + 同名分部文件 + 同名元素，让 code-behind 迁移尽量机械化。拆成 UserControl/页面留到迁移完成后作为独立重构——同时换框架又换结构，出问题无法定位是哪一边引入的。

**B. XAML 必须重写，不能机械转换。**
Avalonia 样式是 CSS 式选择器（`<Style Selector="Button.foo">`），与 WinUI 的 `Style TargetType` + `VisualStateManager` 模型不同。`App.xaml` 的 974 行黑白灰设计系统需整体重建为 Avalonia `Styles` / `ControlTheme`。用 `FluentAvalonia` 补齐 Avalonia 缺失的 `ContentDialog`、`FontIcon`/`SymbolIcon`、`NumberBox`。

**C. 阅读器：把「钩子 + 轮询」反转为标准 JS 桥（本计划最大改动、风险最高）。**

现状（约束 #1）：`IsScriptEnabled=false` 冻结 DOM 事件派发，所有交互靠宿主兜——全局低级鼠标/键盘钩子（`SetWindowsHookEx`，`MainWindow.ReaderFeatures.cs:1172-1400`）、滚动接章 150ms 轮询、选区 300ms 轮询、脚注悬停轮询。`SetWindowsHookEx` 是 Windows 独有，且全局钩子到 Avalonia 控件的坐标换算比 WinUI 更麻烦，无法沿用。

改为：`EpubReaderPreparationService` 生成阅读缓存时对 HTML 做**落盘消毒**——剥离 `<script>`、`on*` 属性、`javascript:`/`data:` URL、外部资源引用，注入 CSP `<meta>` 只放行自己的注入脚本；然后打开脚本开关，用 `postMessage` / `WebMessageReceived` 双向通信。

- 安全性不降反升：EPUB 自带脚本在落盘阶段就已剥离，不再依赖引擎开关这一道防线。file:// 导航白名单（`MainWindow.xaml.cs:3944`）保留。
- 收益：删掉全部钩子与轮询（`ReaderFeatures.cs` 中约 400+ 行），点击分区/选区/滚动接章/脚注悬停全部变成真实 DOM 事件，更准更省电，且天然跨平台。
- 代价：约束 #2（分页 CSS 数学）、#4（导航意图与守卫）、#5（章节切换守卫）必须逐条重新验证。

WebView 引擎本阶段仍用 **WebView2**，通过 Avalonia `NativeControlHost` 承载，藏在 `IReaderHost` 后面。Windows 上渲染表现与现在完全一致（分页 CSS、水波动画不用重调），日后换跨平台 webview 只替换接口实现。

阶段 1 查包时发现 **`Avalonia.Controls.WebView` 12.0.1，owner 是 `avaloniaui`**，即官方第一方 WebView 包。这可能让阅读器直接跨平台，而不必自己包 WebView2。但尚未验证其 API 能力边界（`ExecuteScript`、双向消息桥、自定义 scheme / 资源拦截是否齐全，以及 file:// 导航白名单能否实现），且版本落后主线一个小版本。阶段 4 开始时先做一个小验证再决定：能力够就直接用它，不够再退回 `NativeControlHost` + WebView2。无论走哪条，`IReaderHost` 接口都不变。

> 降级选项：阶段 4 可先原样保留钩子（Windows-only），把 JS 桥推迟到做 Linux 时。代价是这 400 行要迁两次，且跨平台时才暴露分页回归。建议现在就改——迁移期本来就要逐项验证阅读器，两次验证不如一次。

### 10.5 实施阶段

**阶段 0：结构重整（WinUI 版保持可用）— 约 1-2 天**

1. 新建 `src/Kkindle.Platform.Windows`，移入 `WpdKindleAccess.cs`、`WpdSessionCloser.cs`、`ShellFileOperation.cs`、`KindleDeviceService.cs`
2. 从 `AiServices.cs` 抽出 `WindowsDataProtection` → `WindowsSecretProtector : ISecretProtector`；`AiServices` / `KindleEmailServices` / `ZLibraryService` 三处改构造注入
3. 新增 `Kkindle.Core/PlatformServices.cs`
4. `Kkindle.Infrastructure` 与 `Kkindle.Tests` TFM 降为 `net8.0`
5. 现 `Kkindle.App` 改名 `Kkindle.App.WinUI`，引用新平台层

进度（勾选项已编译并通过 191 项测试）：

- [x] 新建 `src/Kkindle.Platform.Windows`，加入 `Kkindle.sln`
- [x] `Kkindle.Core/PlatformServices.cs`：`ISecretProtector`、`IDeviceChangeNotifier`
- [x] `WindowsDataProtection` → `Platform.Windows/WindowsSecretProtector.cs`；`AiSettingsStore` / `KindleEmailSettingsStore` / `ZLibrarySettingsStore` / `AppBackupService` 四处改构造注入
- [x] `NativeDeviceChangeMonitor` → `Platform.Windows/WindowsDeviceChangeNotifier.cs`
- [x] 移入 WPD / shell32 / `KindleDeviceService`；Infrastructure 已无任何 Windows API（`DllImport`、`ComImport`、`Marshal.`、`Shell.Application`、`Registry` 全部无匹配）
- [x] Infrastructure 降 TFM 到 `net8.0`（编译零警告，无 CA1416 平台兼容性问题）
- [x] 拆出 `tests/Kkindle.Tests.Windows` 承接 `KindleDeviceTests.cs`（24 个 `[Fact]`/`[Theory]`，唯一依赖 Platform.Windows 的测试文件），`Kkindle.Tests` 降 TFM 到 `net8.0`
- [x] `Kkindle.App` 改名 `Kkindle.App.WinUI`（随阶段 1 建 Avalonia 项目一并完成；迁移期参照工程仍保留）

阶段 0 完成。四个提交：`1b854e6` 平台抽象 + DPAPI 解耦、`6792e58` WPD/shell32 搬移、`6df5734` Infrastructure 降 TFM、`3a502cf` 测试拆分。

`KindleBookClassifier` 与 `KindleScanCacheStore` 保持 `internal`，靠 `Kkindle.Infrastructure.csproj` 里的 `<InternalsVisibleTo Include="Kkindle.Platform.Windows" />` 跨程序集访问 —— 它们是设备服务的实现细节，不该进 Infrastructure 的公开契约。将来加 `Kkindle.Platform.Linux` 时在同处补一行。

**DPAPI blob 必须字节兼容**：`WindowsSecretProtector` 的 P/Invoke 逐字搬移，包括现在略显名不副实的描述串 `"Kkindle AI API Key"`（DPAPI 把描述当元数据，不参与解密）。动这块会让老用户升级后 API Key、SMTP 密码、Z-Library 登录静默失效。

测试用 `TestHelpers.PlaintextSecretProtector` 替身，不碰系统密钥库，这样设置类测试不绑定机器账户，降 TFM 后可在任意平台跑。

验收：191 项测试全通过；WinUI 版仍能启动，Kindle 设备与 AI 功能正常。**此阶段是纯搬移，不改任何业务逻辑。**

**阶段 1：Avalonia 骨架与设计系统 — 约 3-5 天**

环境事实（2026-08-13 实测）：Avalonia **12.1.1**（支持 `net8.0`）、`FluentAvaloniaUI` **3.0.2**、`Avalonia.Controls.WebView` **12.0.1**（owner 是 `avaloniaui`，第一方包）。模板用 `dotnet new avalonia.app` 生成，默认 TFM 是 `net10.0`，本机 SDK 9.0.315 不支持，需手改为 `net8.0`。

进度：

- [x] 建 `Kkindle.App`（Avalonia 库）+ `Kkindle.Desktop.Windows`（启动头），空窗口可构建
- [x] 自绘方角标题栏（`WindowDecorations=None` + `ExtendClientAreaToDecorationsHint`，三枚矢量 caption glyph）
- [x] 自定义 `ScrollBar` ControlTheme（上下三角、可拖动滑块、透明滑轨、内建自动隐藏）
- [x] `App.axaml` 黑白灰基础设计系统（颜色资源、字体、TextBlock/Button/TextBox 基础样式）
- [x] 内置京华老宋体走 `avares://` 资源（字体文件与许可记录随 `Kkindle.App` 打包）

按「先攻最难的两个控件」策略推进：标题栏和 ScrollBar 是 `App.xaml` 里定制最深的部分，也是 Avalonia `ControlTheme` 与 WinUI `Style` + `VisualStateManager` 差异最大的地方。它们能 1:1 还原，剩下 7 个 `ControlTemplate` 基本没悬念；还原不了则趁早调整设计系统策略，此时只投入了几天而非几周。

- 自绘标题栏：`WindowDecorations=None` + `ExtendClientAreaToDecorationsHint`，替代 `AppWindow` + `DwmSetWindowAttribute`（约束 #7）——Avalonia 原生跨平台，比现方案干净
- 内置京华老宋体走 `avares://Kkindle.App/Assets/Fonts/KingHwaOldSong-v3.0.ttf#KingHwaOldSong`，字体资源在 `Kkindle.App.csproj` 中以 `AvaloniaResource` 打包
- 滚动条自动隐藏（对应 `MainWindow.ScrollbarAutoHide.cs`）改为 `ScrollBarTheme.axaml` 的 `ControlTheme`，全局 `ScrollBar` 设置 `AllowAutoHide=True`、`HideDelay=900ms`

阶段 1 骨架验收（2026-08-13）：`dotnet build Kkindle.sln -c Debug -p:Platform=x64` 0 警告/0 错误；`dotnet test Kkindle.sln -c Debug -p:Platform=x64 --no-build` 共 191 项全通过；Windows Avalonia 启动后保持运行，未发现 XAML 资源加载错误。后续在阶段 2/3 迁移具体视图时，再按需补齐 ComboBox、ListBox、Dialog 等控件的页面级主题。

**阶段 2：本地书库 — 约 1.5-2 周**

`MainWindow.Library.cs` / `LibraryViewModel.cs` / `.Collections.cs` / `.Douban.cs` / `.BookConversion.cs` / `.BookOpening.cs`。含书架/列表/画廊三视图、右键菜单（约 60 处 `MenuFlyout`）、框选多选、黄金分割布局（`ApplyGoldenSidebarWidth`）。`FileOpenPicker` + `InitializeWithWindow`（`MainWindow.xaml.cs:1223`）→ Avalonia `StorageProvider`。

阶段 2 已完成（2026-08-13）：Avalonia 本地书库已落地到 `Kkindle.App`。当前实现包含本地 SQLite 初始化、文件/文件夹导入（EPUB/PDF/MOBI/AZW3）、搜索与作者/标签/格式/分类/阅读状态/收藏/排序筛选、书架/列表/收藏夹三视图、详情元数据编辑保存、收藏与阅读状态操作、动态右键菜单、Ctrl 多选与批量删除、格式打开/删除、Calibre 格式转换入口、豆瓣元数据匹配入口、收藏夹创建/删除/归属切换，以及 Avalonia `StorageProvider` 文件选择器。WinUI 版本保持不变；阶段 2 的“打开书籍”暂时使用系统默认程序，内置 Kreader 留给阶段 4。

阶段 2 验收：`dotnet build Kkindle.sln -c Debug -p:Platform=x64 --no-restore` 通过且 0 警告/0 错误；`dotnet test Kkindle.sln -c Debug -p:Platform=x64 --no-build` 通过 191/191；Windows Avalonia 启动检查保持运行 5 秒后正常退出检查进程。阶段 3 已在同一 Avalonia 主窗口中继续落地。

**阶段 3：Kindle 设备 / 阅读资料 / Z-Library / 设置 / 备份 — 约 1.5-2 周**

阶段 3 Avalonia 进度（2026-08-14）：

- [x] Windows 启动头注入 `IKindleDeviceService` 与 `ISecretProtector`；关闭时停止设备轮询计时器。
- [x] Kindle 书库页：设备检测、书库扫描、发送到 Kindle、导出、删除、安全弹出及设备状态栏。
- [x] Kindle 字体/字典页：目录扫描、导入、导出、删除，并显示路径安全策略。
- [x] 阅读资料页：本地划线/批注 + Kindle 剪贴汇总、来源筛选、全文搜索、删除和 Markdown 导出；筛选使用独立未过滤集合，重复筛选不会丢数据。
- [x] Z-Library 页：格式/语言筛选、分页搜索、详情、下载并导入本地书库；设置页接入账号加密保存与验证。
- [x] 设置与备份页：默认格式、Calibre、网络/自动检测开关、备份导入导出、本地字体/字典、Kindle 邮箱配置；书籍详情提供发送到 Kindle 邮箱入口。
- [x] 修复 Avalonia XAML 初始化期间 `TextChanged`/`SelectionChanged` 提前触发导致的启动崩溃，并完成启动稳定性验证。

阶段 3 验收（2026-08-14）：`dotnet build Kkindle.sln -c Debug -p:Platform=x64 --no-restore` 通过（0 警告/0 错误）；阶段 3 当时的回归集 `dotnet test Kkindle.sln -c Debug -p:Platform=x64 --no-build` 通过 191/191；Windows Avalonia EXE 启动后保持运行 8 秒正常。尚未在本轮连接真实 Kindle、调用真实 Z-Library 或发送真实 SMTP 邮件，需人工验收这些外部依赖闭环。

`MainWindow.KindleTransfer.cs`、`.DeviceResources.cs`、`.DeviceBookExport.cs`、`.ReadingMaterials.cs`、`.ZLibrary.cs`、`.Backup.cs`、`.KindleEmail.cs`、`.Productivity.cs`。自绘控件改用 Avalonia `Control.Render(DrawingContext)`：`MonochromeBarChart`、`RectangularProgressBar`、`TetrisDownloadVisual`；`MarkdownRichTextBlock` 改用 `SelectableTextBlock.Inlines`。

**阶段 4：Kreader 阅读器 — 约 3-4 周（最难）**

阶段 4 当前进度（2026-08-14，核心交互切片）：

- [x] 引入 `Avalonia.Controls.WebView` 12.0.1，完成可替换的 `IReaderHost` 与 `NativeWebViewReaderHost`；两个 WebView 宿主保持挂载，下一章节可后台预加载。
- [x] EPUB 缓存提取后执行 XML 安全解析、HTML/CSS 消毒、外部资源与脚本事件清除、CSP nonce 和最小 `ready` 桥注入；导航默认限制在缓存根目录内，禁用开发者工具和新窗口。
- [x] Avalonia 书库打开 EPUB 时进入阅读表面，支持章节前后切换、恢复章节索引，并将基础章节进度写回 `ReaderDataService`。
- [x] TOC/fragment 语义、滚动/分页布局、字号/行距/正文宽度、黑白外观、当前章搜索、书签、基础划线、进度/排版/阅读时长保存、禅模式与 `postMessage` 事件桥已接入 Avalonia。
- [x] 阅读器关闭路径先保存进度/排版/阅读时长，再取消宿主；分页模式使用横向滚动位置恢复，窗口关闭也执行同一收尾流程。
- [x] 阶段 4 核心切片回归后，Debug x64 测试总数为 192 项，全部通过；Avalonia Windows 启动检查保持运行 8 秒正常。
- [x] Avalonia Windows 已补齐 AI 助手、脚注点击/悬停预览、PDF 文字阅读表面、四档翻页过渡、阅读模式选择、分页分区点击和选区工具栏的功能入口与基本闭环；视觉动画观感、PDF/脚注真实书籍和高 DPI 行为仍需人工验收。

阶段 4 界面/功能对齐（2026-08-14，按旧版 WinUI Kreader 逐项对照）：

- [x] 工具栏改为旧版布局：目录/搜索/A−·100%·A+/分页菜单（滚动·单页·双栏）/更多菜单（禅模式·阅读排版设置·翻页动画 4 档）/划线·批注·书签；PDF 徽标显示在底部进度条旁，PDF 模式隐藏缩放与分页控件并在边界禁用上一章/下一章。
- [x] 新增极简目录 rail（`ReaderTocCompactPanel`，52 DIP）：章节 marker 波峰悬停、悬停标题浮层、上下指示器、滚轮缓动滚动、点击最近 marker 导航；TOC 面板头部新增「切换极简目录」按钮，禅模式默认显示极简目录。
- [x] 新增书签角标（阅读面右上角三角），书签反馈 ToolTip（已添加/已取消书签），瞬态状态提示（2.5s 自动清除），阅读统计「累计阅读 X · 本次 Y」1 秒计时显示。
- [x] F11 进入/退出禅模式、Esc 退出禅模式（与旧版全局钩子一致）。
- [x] 滚动模式滚动到章节边缘自动接章（滚动接章），带连续锁定与短章节跳过；整书搜索恢复「全书 N 段结果 / N 条结果 · PDF 本地文本索引」计数、结果去重、跳转状态文本与关键词黑底白字高亮（`ReaderSearchHighlightTextBlock`）。
- [x] AI 思考深度改为中文标签（自动/快速/平衡/深入，DeepSeek 为 自动/深入/极致），请求期间禁用发送/深度/模型控件，模型列表尝试从 API 刷新（10 秒超时，失败回退静态表）；批注保存增加重叠检测（与旧版一致）。
- [x] 目录/书签/搜索与 AI 对话/划线与笔记标签页恢复旧版空心标签视觉（选中黑框、未选中浅灰框）。

1. `IReaderHost` + `NativeWebViewReaderHost`（Windows backend 使用 WebView2），双 WebView 预加载结构保留
2. `EpubReaderPreparationService` 增加 HTML 消毒 + CSP 注入
3. 四个脚本模块（`ReaderNavigationScripts` / `ReaderPaginationScripts` / `ReaderAppearanceScripts` / `ReaderWaveScripts`）的 CSS/JS 常量原样保留（约束 #8），只改事件接入方式
4. 删除 `ReaderFeatures.cs` 的钩子与轮询，改 `postMessage` 桥
5. 逐项回归约束 #2/#3/#4/#5：分页列边界吸附、图片 contain 拟合、fragment 跳转顶格、章节切换守卫、关闭幂等
6. 禅模式：`AppWindowPresenterKind.FullScreen` → Avalonia `WindowState.FullScreen`
7. `MainWindow.ReaderToc.cs` / `.ReaderInPageSearch.cs` / `.ReaderTools.cs` / `.ReaderAi.cs` / `.ReaderFootnotes.cs` / `.Pdf.cs`

约束 #6（`Slider`/`ComboBox` 事件在 XAML 解析期提前触发导致 `0xC000027B`）是 WinUI 特有；Avalonia 下仍需保留 `AreReaderLayoutControlsReady()` 空值守卫，但崩溃形态不同，需重新确认。

**阶段 5：打包发布 — 约 2-3 天**

已完成发布入口切换：`scripts\Build-Release.ps1` 现在发布 `src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj`，去掉 `WindowsAppSDKSelfContained`，Avalonia 启动头复制 LICENSE、图标和第三方声明，并保留 Calibre 运行时复制与 KFX Input 插件校验流程；Release workflow 增加 Avalonia Windows head 的显式构建。已用 `-SkipInstaller` 实跑生成便携 ZIP 与 `SHA256SUMS.txt`；Inno Setup 安装器路径保持不变。`Kkindle.App.WinUI` 暂不删除，待 AI/脚注/PDF/动画对等和人工验收后作为独立提交移除。

**阶段 6：扩展性验证 — 约 1 天**

Windows 侧 `Kkindle.Core`、`Kkindle.Infrastructure`、`Kkindle.App` 均保持 `net8.0`，可移植层没有发现 Windows UI/WinRT/COM/PInvoke 引用；Debug/Release solution build 与 164 项可移植测试全部通过，Windows 测试另有 28 项通过。当前 WSL 没有安装任何 distribution，因此本机无法执行 WSL 命令；不自动安装发行版，待环境具备后补跑 `dotnet build`/`dotnet test`。此阶段**不**产出 Linux 可运行 GUI。

**粗略总量：约 2-2.5 个月**，阶段 4 占一半且不确定性最高。

### 10.6 WinUI → Avalonia 主要映射

| WinUI 3 | Avalonia | 说明 |
|---|---|---|
| `x:Bind`（166 处） | `{CompiledBinding}` + `x:DataType` | 逐处改写，不能机械替换 |
| `ListView` / `GridView` | `ListBox` + `ItemsPanelTemplate`(`WrapPanel`) | GridView 无直接对应 |
| `FontIcon` / `SymbolIcon` / `NumberBox` / `ContentDialog` | FluentAvalonia | Avalonia 本体没有 |
| `Style` + `VisualStateManager` | `Style Selector` + 伪类（`:pointerover` / `:pressed`） | `App.xaml` 需整体重建 |
| `DispatcherQueue` | `Dispatcher.UIThread` | |
| `AppWindow` / `OverlappedPresenter` / `DwmSetWindowAttribute` | `Window` + `ExtendClientAreaToDecorationsHint` / `WindowState` | 跨平台，比现方案简洁 |
| `FileOpenPicker` + `InitializeWithWindow` | `StorageProvider` | 跨平台 |
| `WebView2` 控件 | `NativeControlHost` 承载 WebView2 | 藏在 `IReaderHost` 后 |
| `SetWindowsHookEx` + 轮询 | `postMessage` JS 桥 | 见 10.4 决策 C |

### 10.7 验证方式

- 每阶段：`dotnet build Kkindle.sln -c Debug -p:Platform=x64`（约定 #12：默认只出 x64 Debug）+ 启动运行
- 回归基线：192 项测试在阶段 0 之后必须始终全绿
- 视觉对照：`docs\images\` 下 9 张截图即验收基线，逐屏对照
- 阶段 4 人工验收（无法自动化，见第 7 节）：分页分区点击、滚动接章、选区工具栏、四种翻页动画观感、禅模式进出；AI/脚注/PDF 需要在对等切片后补验
- 真机验收：Kindle Scribe（MTP）发送/扫描/删除闭环、USB 磁盘型安全弹出
- 阶段 6：Windows 可移植层扫描与 Debug/Release 回归已通过；WSL 因无 distribution 尚未执行

### 10.8 当前 UI / 功能对等审计（2026-08-14）

当前结论：未完成，不得将当前 Avalonia Debug EXE 标记为 WinUI 旧版的 1:1 对等版本。

- [x] 已完成旧版 `src\Kkindle.App.WinUI` 与当前 `src\Kkindle.App` 主窗口 XAML 的静态入口盘点：旧版约 204 个 XAML 事件入口，当前约 147 个，约 57 个旧入口没有在当前主窗口 XAML 中一一对应。由于 Avalonia 可能改用不同事件或代码绑定，这个数字是差异证据，不直接等同于缺失功能数量。
- [ ] 建立旧版所有页面、子项、右键菜单、拖拽、批量操作、设备操作、格式转换和阅读器工具的功能矩阵，并逐项在 Avalonia 中实测闭环。
- [ ] 使用相同窗口尺寸、DPI、字体和数据集，对 `docs\images\` 基线截图逐屏截图并进行像素差异验收；当前只做过局部样式常量对照，没有完成像素级验收。
- [x] 已补齐 AI 助手、脚注浮窗、PDF 阅读表面、四档翻页过渡、阅读模式/排版设置、分页分区点击和选区工具栏的 Avalonia 功能闭环；四档动画与旧版的视觉实现仍不是像素级等价。
- [x] 已重新构建、运行 164 项可移植测试 + 28 项 Windows 测试，并启动 Windows Avalonia Debug EXE 保持运行 8 秒后正常退出测试进程。
- [ ] 仍需用真实 EPUB/PDF、真实 Kindle、相同 DPI 数据集完成手工功能矩阵和截图验收，之后才能把版本标记为 1:1 对等。

### 10.9 风险

1. **分页 CSS 数学**（约束 #2）：列宽 + 间距必须严格等于视口宽，换渲染宿主后 DPI 缩放与 `clientWidth` 取值可能有差异，最可能出现「差一像素、翻页错位」。
2. **JS 桥改造引入阅读器回归**：现有导航守卫（`_readerChapterTransitionSequence` + 取消令牌）是为轮询模型设计的，改事件驱动后时序会变，约束 #4/#5 需重新推演而不是照搬。
3. **FluentAvalonia 定制上限**：`ContentDialog` 等控件的黑白灰定制程度需在阶段 1 就验证，避免阶段 3 才发现改不动。
4. **迁移期双份代码**：`Kkindle.App.WinUI` 保留期间，Infrastructure 的改动要同时满足两边。建议迁移期冻结新功能开发。
5. **硬编码可执行文件路径**（阶段 0 排查时发现，不影响 Windows 版）：`BookFormatConversionService.cs` 有 7 处硬编码 `ebook-convert.exe` / `calibre-customize.exe` 与 `Environment.SpecialFolder.ProgramFiles`。这是运行时路径解析，`net8.0` 下照常编译，所以**阶段 6 的「能编译 + 测试通过」验证抓不到它**；Linux/Mac 上 Calibre 可执行文件无 `.exe` 后缀、也不在 Program Files。真正接 Linux 时需要把候选路径表按平台分支。同类问题还需排查数据目录与设备挂载点假设。

