# Kreader 阅读器功能对比：老版 WinUI vs 新版 Avalonia

> 本文是迁移期间完成的历史审计记录：删除前的 WinUI 参照版（MainWindow.xaml 阅读器区 3589–5208 + 9 个 Reader 分部类 + 4 个脚本文件）与 `src/Kkindle.App`（MainWindow.axaml 阅读器区 3118–4221 + 7 个 Reader 分部类 + 基础设施）曾逐项比对。旧 WinUI 源码已于 2026-08-19 删除，文中旧版行号仅用于追溯当时的审计结论。

## 结论摘要

- **核心功能已全部移植**：双宿主章节预加载、进度恢复/保存、目录/书签/极简目录轨、全书搜索与页内搜索、选区工具条 6 项、标注（5 色 6 样式/重叠保护/注入/导出）、AI 助手（3 提供商/推理深度/流式+思考/来源跳转）、脚注悬停弹窗、排版设置、4 种翻页动画、禅模式、阅读统计、PDF 文本搜索/进度/书签/标注、F11/Esc/Ctrl+F/Ctrl+B 快捷键。
- **22 项缺口已按本清单全量移植（2026-08-14）**：P0 徽标 bug、P1 输入层 6 项、P2 渲染差异 4 项、P3 行为差异 9 项、架构 2 项的可行部分。各项处理方式见下文各表"移植状态"列与文末完成记录。
- 遗留架构限制（有意保留）：PDF 点击区翻页在 WebView2 内置查看器下不存在（键盘/按钮/滑块/滚轮可用）；波浪动画为 CSS 近似（无 CDP 快照）；`IsScriptEnabled=false` 基线无法恢复（净化+CSP nonce 兜底）。
- 老版本来就没有的（AI 复制/插入、脚注跳转/列表、翻译/分享、TTS/自动滚动/截图、深色主题等）**不属于缺口**，见"澄清项"。
- 老版本来就没有的（AI 复制/插入、脚注跳转/列表、翻译/分享、TTS/自动滚动/截图、深色主题等）**不属于缺口**，见"澄清项"。

---

## 一、未移植 / 新版失效的功能（按优先级）

### P0 —— 明确可见缺陷

| # | 缺口 | 老版（参照） | 新版现状 |
|---|------|-------------|---------|
| 1 | **PDF 底部"PDF"徽标永不显示（bug）** | `MainWindow.xaml:4488` 外框 Border 的 Visibility 用 `{Binding Visibility, ElementName=ReaderPdfBottomText}` 联动，切 PDF 即显示 | `MainWindow.axaml:3800-3806` 外框 Border 硬编码 `IsVisible="False"`，代码 `ReaderInteraction.cs:1426-1427` 只切内层 TextBlock → 徽标永远隐藏。修复：给外框命名并在 `UpdateReaderToolbar` 中联动 |

### P1 —— 输入/交互缺失（老版有、新版无）

| # | 缺口 | 老版（参照） | 新版现状 |
|---|------|-------------|---------|
| 2 | **分页模式滚轮翻页** | 低层鼠标钩子 `WH_MOUSE_LL` 把滚轮累计换算成翻页（`ReaderFeatures.cs:1301-1331`，120 单位=1 页，并吞掉浏览器二次滚动） | 全代码无滚轮→翻页逻辑（仅极简目录轨有滚轮处理）；分页模式下滚轮无效。需在桥接 JS 加 wheel 上报或宿主处理 `PointerWheelChanged` |
| 3 | **连续滚动模式键盘导航** | ←/→ 翻章、↑/↓ 72px 平滑滚动（`xaml.cs:3625-3684`） | 桥接 `key` 消息只在 PDF 或分页模式处理（`ReaderInteraction.cs:782-797`），滚动模式下方向键全部无效 |
| 4 | **PDF 点击左右区域翻页（已澄清）** | 老版钩子点击区（左 1/3、右 2/3，`ReaderFeatures.cs:1272-1300`），老版 PDF 默认分页模式（`_readerFlowMode=1`） | **非缺口**：PDF 渲染脚本无条件发 `page` 消息且宿主含 `_readerIsPdf` 分支，点击区本就工作；切真实 PDF 渲染后由 Chromium 内置查看器接管，键盘/‹›/滑块/滚轮可翻页（点击区为架构限制，见 AI_HANDOFF 第 7 节） |
| 5 | **禅模式阅读区鼠标唤醒顶栏（待实测）** | 低层鼠标钩子 `WM_MOUSEMOVE` 唤醒（`ReaderFeatures.cs:1251-1271`），覆盖 WebView2 组合岛盲区 | 新版靠 `ReaderRoot_PointerMoved`（Avalonia 指针事件），WebView2 覆盖区的事件不进入 Avalonia，桥接 JS 也没有 pointermove 上报 → 阅读区移动鼠标大概率不唤醒顶栏。需真机验证 |
| 6 | **键盘选区不触发选区工具条** | 轮询选区（`GetReaderSelectionStateScript`，`ReaderTools.cs:1194-1263`），键盘选区（Shift+方向键）也能弹出工具条 | 只在 `mouseup` 上报选区（桥接 JS:119），键盘选中的文字不弹工具条 |
| 7 | **划线样式快速选择器（悬停弹出）** | 选区条"划线▾"悬停弹出样式 flyout（直线/双线/虚线/点线/波浪/荧光，`ReaderTools.cs:151-212, 1351-1374`） | "划线"按钮直接按默认样式保存（`ReaderAnnotations.cs:170-171`），无快速选择 |

### P2 —— 渲染/视觉差异

| # | 缺口 | 老版（参照） | 新版现状 |
|---|------|-------------|---------|
| 8 | **PDF 显示降级（最大功能差）** | 直接把 PDF 文件喂给 WebView2 内置 PDF 查看器，真实版式/图片/公式/矢量（`MainWindow.Pdf.cs:72-73`，`Source = 文件 URI + #page=N`） | 用"文本化 HTML"重建：纯文本 + 页卡片边框（`ReaderFeatures.cs:162-248`），无原版排版、图片、公式；搜索/进度/书签/标注逻辑一致但显示完全不同 |
| 9 | **波浪翻页动画为近似实现** | DevTools Protocol `Page.captureScreenshot` 截取页面快照做真实卷页（`xaml.cs:2568, 3530-3565`） | CSS 灰度/对比度滤镜脉冲（`ReaderInteraction.cs:989-1026`），无真实卷页 |
| 10 | **AI 气泡不渲染 Markdown** | `MarkdownRichTextBlock` 渲染（`ReaderAi.cs:569`） | 纯 `TextBlock` 显示原文（`MainWindow.axaml:3901`），AI 回复中的 **加粗/代码/链接** 以原文展示 |
| 11 | **正文外观覆盖 CSS 简化（选区/图片/封面/两端对齐）** | 注入约 200 行覆盖样式（`xaml.cs:4422-4621`）：html font-size 百分比、`::selection` 黑底白字、两端对齐、letter-spacing、链接色 #222、p/h1-h4/blockquote 边距、图片适配（分页 contain + max-height、`.kkindle-cover` 封面检测）、pre/table 横向滚动、ruby、字体预热 `fonts.load`、`.kkindle-fragment-break` 断列 | 仅注入滚动条 + 分栏 + 白底 + 字号/行距/字体（`ReaderInteraction.cs:167-229`）：选区颜色、链接色、两端对齐、图片与封面在分页模式的表现与老版不同 |

### P3 —— 行为/精度差异

| # | 缺口 | 老版（参照） | 新版现状 |
|---|------|-------------|---------|
| 11 | **标注重定位精度** | prefix/suffix 前后文锚定评分定位（`ReaderFeatures.cs:582-615`） | 只按选中文本首个命中注入（`ReaderInteraction.cs:329-394`）；同引文多次出现时可能标错位置。数据已存 Prefix/Suffix（`ReaderAnnotations.cs:70-71`）但注入时未使用 |
| 12 | **词典交互简化** | 弹窗显示全部词典条目（`Productivity.cs:368-377`） | 状态栏只显示第一条释义，2.5s 消失（`ReaderAnnotations.cs:197-205`） |
| 13 | **复制后不清除正文选区** | 复制后执行 `ClearReaderSelectionAsync` 清除 DOM 选区（`ReaderTools.cs:1345-1347`） | 复制后选区保留（`ReaderAnnotations.cs:162-168`） |
| 14 | **Esc 层叠关闭不全** | Esc 依次关闭：全书搜索面板 → 排版浮层 → 禅模式（`xaml.cs:3579-3597`） | 已移植：Esc 依次关闭搜索面板 → 排版浮层 → 禅模式（`MainWindow_KeyDown`） |
| 15 | **书签反馈方式（已澄清）** | 书签按钮旁专用 ToolTip（`ReaderTools.cs:624`） | **非缺口**：`ShowReaderBookmarkFeedback`（1.6s ToolTip）已实现，此条为误报 |
| 16 | **按钮 ToolTip 覆盖（个别缺失）** | 全窗口视觉树补丁服务自动为所有缺失 ToolTip 的交互控件生成提示（`MainWindow.ToolTips.cs`，含阅读器全部按钮） | 全部改为 XAML 手写 `ToolTip.Tip`，阅读器主要按钮已覆盖（20+ 处），但个别按钮（如缩放 ±、AI 预设按钮）无提示 |
| 17 | **进度滑块拖动提示缺失** | 代码接线 `ThumbToolTipValueConverter = new ReaderProgressToolTipValueConverter(GetReaderProgressSliderLabel)`（`xaml.cs:265-266`），拖动时显示 "{current} / {total} · 章节名"（PDF："第 N 页"） | 无任何滑块 ToolTip（XAML 与代码均无） |
| 18 | **字体项与字体栈差异** | 字体框 7 项（含 **等线 DengXian**）；内置字体 `KingHwaOldSong-v3.0.ttf`（WinUI 项目资源）；`BuildReaderFontStack` 构造回退栈（内置宋体+思源+Noto+雅黑+sans-serif，`ReaderTools.cs:1634-1657`） | 字体框仅 6 项（缺等线）；内置字体未随新版打包（csproj 无 ttf）；仅注入单一 `font-family`，无回退栈 → 选"京华老宋体"实际回落系统字体 |
| 19 | **滚动接章短章节连跳缺失** | 切章后 60ms 检测章节不可滚动（高度差 ≤16px）自动连跳（`SkipShortChapterIfNeededAsync`，`ReaderFeatures.cs:1492-1514`） | 只有 500ms 强制推进（`ReaderInteraction.cs:871-885`），短章节可能停在空页 |

## 二、架构性差异（需决策，非简单缺失）

| # | 差异 | 老版 | 新版 |
|---|------|------|------|
| 16 | **WebView2 安全/行为配置** | `IsScriptEnabled=false` + 禁用 DevTools/状态栏/脚本对话框/默认右键菜单（`xaml.cs:2119-2130`）；脚本用用户脚本机制注入 | 仅 `EnableDevTools=false`（`NativeWebViewReaderHost.cs:74-77`）；Avalonia `NativeWebView` 无公开设置。桥接依赖 `InvokeScript`（必须开脚本）；Chromium 默认英文右键菜单未被禁用（正文右键会出现 Chromium 菜单） |
| 17 | **导航意图管线未接线** | Core `ReaderNavigationIntent/ReaderNavigationLocationPolicy`（`ReaderModels.cs:200-286`）区分"目录/进度跳转归一化章节起始"与"书签/批注/搜索/AI 源保留偏移" | `NavigateToReaderItemAsync` 无 intent 参数，无 fragment 导航一律 `NormalizeChapterStart`（`ReaderInteraction.cs:242-327`），书签恢复/批注跳转可能破坏基于 DOM 文本的偏移计算 |

## 三、代码卫生（死代码/残留）

- 阅读器内 AI 设置面板整块死 UI：`ReaderAiSettingsView`（`MainWindow.axaml:4017`）恒 `IsVisible=False`（`ReaderInteraction.cs:152`）；`ReaderAiSettingsOpenButton_Click`（`ReaderAi.cs:163`）无 XAML 接线（孤儿）；`ReaderAiSettingsSaveButton_Click/CancelButton_Click`（`ReaderAi.cs:166-203`）不可达。真实入口已改为主设置页（设计决策，建议清理死代码或补接线）。
- `_readerAiSettingsVisible` 恒为 false（`ReaderAi.cs`），模型选择逻辑实际恒写设置——残留判断，可删除。

## 四、澄清项（老版也没有，不属于缺口）

- AI 复制/插入/重新生成按钮：老版 AI 无（仅清空对话），属路线图项
- 脚注点击跳转原文/返回/列表：老版也只有悬停弹窗（`ReaderFootnotes.cs`）
- 选区条翻译/分享/网页搜索：老版无（6 项与新版一致）
- TTS 朗读、自动滚动、截图、深色主题、书籍信息对话框：老版 `ReaderTools.cs` 均不含（逐项核对过）
- PageUp/PageDown：老版无（只有方向键）；新版反而支持
- 滚动条自动隐藏机制：老版 `ScrollbarAutoHide.cs` 900ms 空闲后整体隐藏滚动条（豁免 ReaderTocList）；新版 `ScrollBarTheme.axaml` 用 Avalonia 标准 `IsExpanded` 机制（轨道/按钮常隐、滚动时展开、细拇指常显）——像素轮已确认的视觉设计，非功能缺口

## 五、待人工验证

1. 禅模式进入后，鼠标在正文（WebView 覆盖区）移动能否唤出顶栏（缺口 #5）
2. 修复 #1 后 PDF 徽标显示
3. 波浪/滑动动画与老版视觉对比（#9）
4. 竖排、双页模式实际渲染与老版对比
5. Chromium 默认右键菜单是否可接受（#16）

## 六、建议实施顺序

1. P0：#1 PDF 徽标（一行改动）
2. P1：#2 滚轮翻页 → #3 滚动模式键盘 → #4 PDF 点击区（均为桥接 JS + 宿主处理，改动集中）
3. P1：#7 划线样式选择器、#6 键盘选区轮询
4. P2：#10 AI Markdown 渲染 → #9 波浪动画 → #11 正文外观 CSS
5. P3：#11 标注锚定、#12 词典弹窗、#14 Esc 层叠、#17 滑块提示、#18 字体、#19 短章节连跳
6. 架构：导航意图管线（#17）、WebView 配置（#16）（需评估 Avalonia NativeWebView 能力边界）

---

## 八、移植完成记录（2026-08-14）

全部 22 项缺口已按上述顺序移植完毕（`dotnet build` 0 错误、192 项测试全绿、EXE 冒烟通过，随 AI_HANDOFF 一并提交）。逐项处理方式：

| # | 缺口 | 处理方式 |
|---|------|---------|
| 1 | PDF 徽标（bug） | 外层 Border 命名 `ReaderPdfBadge`、去硬编码隐藏，`UpdateReaderToolbar` 切换徽标整体 |
| 2 | 分页滚轮翻页 | 桥接 `wheel` 消息（分页模式 preventDefault）+ 宿主 120 单位累计翻页（对应 WH_MOUSE_LL） |
| 3 | 滚动模式键盘 | 桥接 `key` 滚动模式分支：←/→ 翻章、↑/↓ 72px 平滑滚动（keydown 保留重复语义） |
| 4 | PDF 点击区翻页 | **非缺口**：PDF 渲染脚本无条件发 `page` 消息且宿主含 `_readerIsPdf` 分支，文本 HTML 下本就工作；切真实 PDF 渲染后由 Chromium 查看器接管（键盘/按钮/滑块/滚轮），点击区不复存在（架构限制，见第 7 节） |
| 5 | 禅模式鼠标唤醒 | 桥接 80ms 节流 `pointermove` 消息唤醒 chrome（覆盖 WebView2 HWND 岛盲区） |
| 6 | 键盘选区工具条 | 桥接 `selectionchange`（rAF 防抖）上报，Shift+方向键选区可弹工具条 |
| 7 | 划线样式选择器 | 「划线 ▾」悬停展开 6 样式 MenuFlyout + 240ms 关闭宽限，选中同步样式框并保存 |
| 8 | PDF 真实渲染 | `OpenPdfReaderAsync`/`NavigatePdfPageAsync` 直接导航 file:// + #page=N（WebView2 内置查看器）；删除文本 HTML 渲染；标注留在笔记列表并跳页；文本索引驱动搜索/进度/AI |
| 9 | 波浪动画 | CSS 增强：灰度脉冲 + 380ms 斜向扫光条带；CDP 快照卷页未移植（adapter 仅 COM 指针，无托管 CDP） |
| 10 | AI Markdown | 新增 `KreaderMarkdownTextBlock`（标题/列表/引用/代码块/粗斜体/行内代码/链接），替换气泡纯文本 |
| 11 | 正文外观 CSS | `BuildReaderAppearanceCss`：::selection 反色、两端对齐、链接色、p/h/引用间距、图片约束、pre/table 横滚、hr、ruby、fragment-break 断列 |
| 12 | 词典弹窗 | `ShowMessageAsync` 显示全部词典条目（旧版同款） |
| 13 | 复制清选区 | 复制后 `removeAllRanges()` + 收起工具条 |
| 14 | Esc 层叠 | 搜索面板 → 排版浮层 → 禅模式 |
| 15 | 书签反馈 ToolTip | **非缺口**：`ShowReaderBookmarkFeedback`（1.6s ToolTip）早已实现，报告此条为误报，已删除 |
| 16 | 按钮 ToolTip | 缩放 A−/A+ 补 ToolTip.Tip；其余已手写覆盖 |
| 17 | 滑块拖动提示 | 拖动时 ToolTip「current / total · 章节名」（PDF「第 N 页」） |
| 18 | 字体项与字体栈 | 补等线 DengXian（7 项）；内置 KingHwaOldSong 进 `Kkindle.App\Assets\Fonts`（AvaloniaResource + Content）；`BuildReaderFontStack` 回退栈 + @font-face 注入 |
| 19 | 短章节连跳 | 滚动模式切章后 60ms 检测不可滚动（≤16px）连跳，深度上限 5 |
| 20 | 导航意图管线 | `NavigateToReaderItemAsync` 加 intent 参数，`ApplyReaderLocationAsync` 按 `ReaderNavigationLocationPolicy` 归一化/锚点/保持 DOM；批注跳转附带 fragment |
| 21 | WebView2 配置 | adapter 仅暴露 COM 指针（无托管设置对象）：右键菜单改 `contextmenu` preventDefault（页面级等效）；DevTools 已禁；脚本对话框被净化+CSP nonce 消解；`IsScriptEnabled=false` 无法恢复（桥接依赖 InvokeScript） |
| 22 | 代码卫生 | 删除恒 false 的 `_readerAiSettingsVisible` 残留；ReaderAiSettingsView 死 UI 保留（入口在主设置页） |

遗留限制（有意保留，见 AI_HANDOFF 第 7 节）：PDF 点击区翻页、CDP 快照卷页、`IsScriptEnabled=false` 基线、竖排仅滚动模式。
