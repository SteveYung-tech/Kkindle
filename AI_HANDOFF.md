# Kkindle 项目交接文档

> 给后续 AI / 开发者使用。继续工作前请先阅读本文档，再查看代码和当前 Git 状态。
>
> 更新时间：2026-08-13
>
> 项目目录：`C:\Users\kings\Desktop\01_Projects\Kkindle`

## 0. 当前状态

- 阶段：P0/P1/P2 全部完成；本地书库、Kreader 阅读器、阅读资料中心、Kindle 设备管理（USB/WPD/MTP）、格式转换、Z-Library、Kindle 邮件、备份/设置、AI 助手、安装包与 GitHub 自动发版均已实现并验证。
- 分支 `master`；`v0.5.2`（提交 `5daf140`）已推送至 `origin/master` 并自动发版，当前本地包含尚未推送的滚动条修复提交。
- 最新版本：0.5.2（标签 `v0.5.2`）；在 0.5.1 基础上新增全应用滚动条自动隐藏，滚动或悬停时显示，空闲后淡出；补齐 Popup、折叠面板、ContentDialog 和延迟生成模板的挂载，并隔离嵌套 ScrollViewer 的滚动条归属。
- 测试：Release x64 191 项全部通过（0 失败、0 跳过，2026-08-13）。
- 本地最近完整测试包（2026-08-12 19:36，版本 `0.5.0-test.1`，由 `685ab20` 发布；内置 Calibre 运行时与 KFX Input 插件，启动/关闭验证通过；如需包含 0.5.2 改动请重新发布）：
  - exe：`artifacts\Kkindle-0.5.0-test.1\Kkindle-0.5.0-test.1-win-x64\Kkindle.exe`
  - 便携包：`artifacts\Kkindle-0.5.0-test.1\Kkindle-0.5.0-test.1-win-x64-portable.zip`
- 常规发布目录：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`（其中 exe 仍是 2026-08-10 基线，如需随最新提交刷新请重新发布）。
- 真机验证：Kindle Scribe（MTP）EPUB 发送/扫描/删除闭环、64 MiB 大文件传输、设备字体/字典读写均已验收，设备端无测试残留。
- 开发约定：代码修改必须能编译；每次发布 EXE 只创建一个对应 Git 提交；文档随代码一并提交。

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
├─ src\Kkindle.App/              # WinUI 3 窗口、页面、阅读器宿主与注入脚本
├─ src\Kkindle.Core/             # 领域模型、策略（阅读选择/导航/分页/转换/设备型号目录）
├─ src\Kkindle.Infrastructure/   # SQLite、元数据、Kindle 设备、Calibre、AI、备份、Z-Library
└─ tests\Kkindle.Tests/          # 单元测试
```

应用数据目录在 exe 旁：`data\{library,covers,reader-cache,logs,kkindle.db}`、`backups`、`app-root.json`。

## 3. 已实现功能

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
- Kreader 全书搜索结果滚动条支持自动隐藏，搜索结果卡片右侧留出滑块避让；左侧底部“返回书架”按钮铺满整块底栏并与右侧底栏同高。

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

- 当前 `master` 工作区干净；本地比 `origin/master` 超前 4 个提交（均未推送）；构建输出由 `.gitignore` 排除。
- 约定：一次 exe 发布对应一次 Git 提交；每次代码/文档改动随 AI_HANDOFF 一并提交；继续工作前先 `git status --short --branch`。
- GitHub：`git@github.com:kingstacker/Kkindle.git`。
