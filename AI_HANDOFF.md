# Kkindle 项目交接文档

> 给后续 AI / 开发者使用。继续工作前请先阅读本文档，再查看代码和当前 Git 状态。
>
> 更新时间：2026-08-07
>
> 项目目录：`C:\Users\kings\Desktop\01_Projects\Kkindle`

## 0. 当前状态速览

- 当前阶段：P0、P1 已完成；P2 自动化和真机大文件传输已完成；内置阅读器已完成三栏界面重设计，并在阅读助手中新增本地书库索引、AI 问答和划线/批注。上一轮完成读者生产力工具大升级（阅读排版设置、进度断点恢复、书签、书内搜索、选区快捷工具栏、划线/批注导出、阅读统计、CJK 阅读增强），并修复了 WebView2 `IsScriptEnabled=false` 下真实交互失效的两个问题（连续滚动接章、分页点击翻页）与分页正文排版（默认横排分页每屏一个完整视口列、列宽对齐、列边界吸附、排版数据安全回退）。再上一轮修复 EPUB 图片/封面显示：分页模式下封面/大型插图按比例 contain 约束在当前正文内容盒内，滚动模式图片宽度跟随正文内容并保持比例、无横向溢出。再上一轮修复阅读器顶部自绘 X 退出按钮点击卡死/无响应（根因：低级鼠标钩子回调跨线程访问 XAML 与 `UnhookWindowsHookEx` 双向死锁 + 窗口关闭同步等待永不返回的 WebView 脚本；改为钩子只读缓存/投递 UI 线程、关闭流程幂等非阻塞、有界异步落库），并为所有真实章节切换路径加入平滑过渡。上一轮修复目录跳转/章节切换时的闪现与短暂卡顿（根因：旧实现在 `ReaderWebView.Source = target` 前先淡出/滑出旧内容，且 NavigationCompleted 的批注/脚注/进度等后置工作全部在入场动画前串行等待；改为导航期间保持旧内容可见、首屏准备完成后再短淡入/滑入，非首屏任务延迟到显示后并带导航序列守卫，杜绝旧导航/旧后置任务覆盖新章节）。上一轮修复目录跳转后正文未从新章节第一行开始（根因：目录跳转没有明确导航意图，旧 pending 位置/旧"移动章末"意图/同章节空操作会把新章滚到旧位置；改为 `ReaderNavigationIntent` 明确意图 + 导航前清理旧 pending 定位 + 目录/进度条跳转显式把滚动容器复位到章节首行：滚动模式 `scrollTop=0`、分页模式吸附第一列边界 `scrollLeft=24/top=0`，带 fragment 的目录锚点仍跳锚点，同章节点击也复位并重对齐滚动边沿）。再上一轮修复"scrollTop=0 但有的章节仍不顶格"（根因：`scrollTop=0` 只复位滚动容器，无法消除首元素自带的上边距/首行空白节点——真实《規模》的 EPUB 样式 `div.chatu-part{margin-top:30%}` 作用于全部部页/致谢/注释开篇，实测滚动模式首元素 marginTop 199.2px、分页 233.275px；改为目录/进度条/普通章节切换在复位滚动前先做章节开头安全归一化，带 fragment 或搜索/书签/批注/AI 目标不归一化）。**本轮修复"子章节 fragment 跳转后标题停在页面中部、顶部仍显示上一段正文"**（根因：fragment 定位旧实现只 `scrollIntoView` + 分页吸附，子章节与上一段正文处在同一个 CSS 列时标题停在列中部（报告例 `09.xhtml#sigil_toc_id_68` 分页 rectTop 256.38→大窗口 226.94，`scrollTop=0` 无效）；改为 fragment 导航专属定位：分页模式给目标块临时加 `.kkindle-fragment-break`（`break-before: column !important`）强制标题从新列顶部开始、归零目标块 margin-top 使标题贴内容起始线、`scrollLeft` 吸附目标列；滚动模式按正文内容盒顶部对齐（保留 body 阅读 padding）；隐藏/空目标向前找第一个有效标题/段落/图片，找不到安全回退章节首行；普通章节导航清理临时断点恢复原排版。实测 6 个真实子章节 × 滚动/分页全部顶格）。详见第 30 节。
- 当前分支：`master`；最新本地提交为本轮最终提交 `fix: align subchapter navigation`（详见第 30 节）。
- GitHub：`origin/master` 仍为 `4e8009b`，本地领先 19 个提交，按开发约定未自动推送。
- 最新便携版：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`，exe 更新于 2026-08-07 本轮发布（15:41）。
- 最新源码验证：Debug/Release x64 完整解决方案构建均为 0 警告、0 错误；74 项测试全部通过（新增 `ReaderNavigationLocationTests` 子章节 fragment 优先级 / 空 fragment 安全 4 项）。Release 已重新发布，并用真实中文 EPUB《規模/Scale》（发布数据 `data/library/6efd4f1ba0ed4a2abdc2f0390edc7299/…(z-library.sk,…).epub`，SVG 封面 `cover.jpg` 1536×2048）做真实运行定向验证：WebView2 复现工具用生产注入 CSS + 生产 fragment 定位脚本对 6 个真实子章节 fragment（`01.xhtml#sigil_toc_id_1/5`、`02.xhtml#sigil_toc_id_10`、`03.xhtml#sigil_toc_id_24`、`05.xhtml#sigil_toc_id_38`、报告例 `09.xhtml#sigil_toc_id_68`）逐一读取真实 DOM：滚动模式标题 rectTop 57.78–58.28（正文内容盒起始线 58，修复前 0.23/0.29/-0.37 贴窗口边）；分页模式标题 rectTop=56（新列顶部，修复前 226.94–538.44 停在列中部）、目标列左边界 0、`scrollLeft` 吸附列边界；报告例截图对应验证（09.xhtml#68：分页 256.38→56，滚动 0.23→58.08）；快速连续子章节点击只保留最后一次目标的断点（breakCount=1，前一次目标的断点已清除）；普通章节导航归一化后断点清除、标题回到自然位置（原排版恢复）。Release 发布版 UIA 端到端：启动存活、打开真实 EPUB（恢复 21/31）、目录子章节 fragment 跳转（`frag-01-1` 21→5、`frag-02-10` 5→7、报告例同章 fragment 重定位不卡）、快速连续 3 次目录点击最终只落在最后一次目标且 2 秒后计数稳定、全程无冻结、X 关闭 190ms（详见第 30 节）。
- 真机验证：Kindle Scribe 上的真实 EPUB 已完成发送、重新扫描和删除闭环；2026-08-06 又完成 64 MiB EPUB 发送、大小校验和删除，设备端无测试残留。
- 开发约定：后续代码修改必须编译；每次重新发布 EXE 只创建一个对应 Git 提交。

## 1. 项目目标

Kkindle 是一个只供个人使用的 Windows 11 Kindle 书库与 USB 管理器，产品体验参考用户提供的 Reeden 截图：

- 灰白、黑字、纸张感的简约界面
- 左侧固定导航栏，中间书架，右侧书籍详情
- 默认中文
- 主要操作简单、安静，不追求复杂动画
- 独立管理本地图书，不读取或修改 calibre 数据库

首版技术路线：

- C# + .NET 8
- WinUI 3 + Windows App SDK
- CommunityToolkit.Mvvm
- SQLite + `Microsoft.Data.Sqlite`
- x64、unpackaged、self-contained、便携目录发布

## 2. 原始 v1 计划

### 2.1 项目结构

```text
Kkindle/
├─ Kkindle.sln
├─ Directory.Build.props
├─ README.md
├─ AI_HANDOFF.md
├─ src/
│  ├─ Kkindle.App/              # WinUI 3 页面、窗口和交互
│  ├─ Kkindle.Core/             # 领域模型、服务接口、任务模型
│  └─ Kkindle.Infrastructure/   # SQLite、文件、元数据、Kindle 设备
└─ tests/
   └─ Kkindle.Tests/            # 单元测试和虚拟设备测试
```

应用运行目录旁边的数据目录预期为：

```text
Kkindle.exe
data/
├─ library/
├─ covers/
├─ kkindle.db
└─ logs/
```

### 2.2 书库功能

需要支持：

- 导入文件和文件夹
- EPUB、PDF、MOBI、AZW3
- 导入时复制到应用自己的 `data/library`
- SHA-256 去重
- 同一本书支持多个格式
- EPUB 读取标题、作者、系列、简介和封面
- PDF 读取基础文档信息
- MOBI/AZW3 尝试读取基础元数据；失败时使用文件名
- 手动编辑标题、作者、系列、标签、简介和封面
- 关键词搜索
- 作者、标签、格式筛选
- 书架视图和列表视图
- 删除书籍时删除 Kkindle 自己管理的文件
- 单个损坏文件失败时，不能中断整批导入

核心模型：

```text
Book
├─ Id
├─ Title
├─ Authors
├─ Series
├─ SeriesIndex
├─ Description
├─ Tags
├─ CoverPath
├─ CreatedAt
└─ UpdatedAt

BookFile
├─ Id
├─ BookId
├─ Format
├─ RelativePath
├─ Size
└─ Sha256
```

### 2.3 Kindle USB 功能

首版不写 USB 驱动，也不实现 Kindle 专用通信协议。Kindle 通常作为 Windows 可移动磁盘出现，应用只操作设备的 `documents` 目录。

服务接口：

```text
DetectDevicesAsync()
ScanBooksAsync(device)
SendBookAsync(device, bookFile)
RemoveBookAsync(device, deviceBook)
EjectAsync(device)
```

识别策略：

1. 扫描可移动磁盘。
2. 只接受根目录存在 `documents` 的设备。
3. 记录卷序列号和设备名称，盘符变化不应影响识别。
4. 监听插拔事件；若稳定性不足，首版可先用轮询保证可靠性。

安全边界：

- 只读写 Kindle 的 `documents` 目录。
- 不访问或修改 `system` 等目录。
- 不读写 Kindle 内部数据库。
- 不处理或破解 DRM。
- Kindle 中原有但非 Kkindle 发送的文件只读展示，删除前必须二次确认。

发送流程：

```text
选择书籍
→ 检查设备
→ 检查目标文件
→ 复制到临时文件
→ 校验大小和 SHA-256
→ 原子改名
→ 更新同步记录
```

冲突规则：

- 目标已有相同哈希：跳过复制，视为已同步。
- 文件名相同但内容不同：不覆盖，生成带序号的新文件名。
- 复制中断：清理临时文件，不改变本地图书库状态。
- 设备拔出：停止任务并显示原因。
- 安全弹出失败：提示用户用资源管理器手动弹出。

首版不做格式转换、阅读进度、笔记、收藏、集合或同步服务。

### 2.4 UI 计划

页面计划：

- `LibraryPage`：书架、搜索、筛选和视图切换
- `BookDetailPane`：书籍详情、元数据编辑和操作
- `ImportPage`：导入进度和失败列表
- `DevicePage`：Kindle 状态、容量和设备书籍
- `SettingsPage`：书库位置、设备绑定和基础设置

视觉约束：

```text
页面背景：#FFFFFF
侧边栏：#FFFFFF
选中项：#000000
主文字：#000000
辅助文字：#5A5A5A
强调色：#000000
边框：#000000
```

- 侧边栏宽度约 188–220px
- 搜索框和按钮都使用刚性矩形
- 所有控件、面板和封面占位均为 0 圆角
- 白色作为主界面基底，黑色只用于 Logo、当前入口、状态和主操作等小色块
- 本地书库与 Kindle 书库使用独立入口和独立页面标题，不能混为一个列表
- 不使用渐变和强阴影
- 封面保持固定比例
- 鼠标悬停封面时显示标题、作者、格式、文件数、系列、标签和简介
- 空书库显示纸张风格空状态
- 支持窗口缩放、高 DPI、100%/150%/200% 缩放
- 使用系统标题栏，不自行绘制窗口控制按钮

### 2.5 后台任务和错误处理

导入、扫描、复制、删除都不能阻塞 UI，需要支持：

- 进度
- 取消
- 失败重试
- 逐项错误报告
- 日志
- 成功数、失败数和失败原因

## 3. 当前已完成

### 3.1 工程和依赖

- 已创建 `Kkindle.sln`。
- 已创建 Core、Infrastructure、App、Tests 四个项目。
- `Directory.Build.props` 统一使用 .NET 8、x64 和 Windows 目标框架。
- App 使用 `Microsoft.WindowsAppSDK` 2.3.1。
- App 使用 `CommunityToolkit.Mvvm` 8.4.2。
- Infrastructure 使用 `Microsoft.Data.Sqlite` 10.0.10。
- App 当前配置为 `WindowsPackageType=None`、`win-x64`。
- 已加入 `app.manifest`。

### 3.2 Core

`src\Kkindle.Core` 已包含：

- `Book`
- `BookFile`
- `KindleDevice`
- `KindleBook`
- `BookMetadata`
- `ImportBatchResult`
- `TransferProgress`
- `IBookLibraryService`
- `IMetadataService`
- `IKindleDeviceService`

### 3.3 本地书库和元数据

`src\Kkindle.Infrastructure` 已实现：

- `AppPaths`：便携目录下的 `data`、`library`、`covers`、数据库路径。
- `Hashing`：SHA-256 异步计算。
- `SqliteBookLibraryService`：
  - 初始化 SQLite 表和索引。
  - 导入单文件或文件夹。
  - 导入时计算哈希并复制到应用目录。
  - 相同 SHA-256 跳过重复文件。
  - 按标题和作者合并同一本书的其他格式。
  - 使用 `.part` 临时文件，完成后改名。
  - 保存文件相对路径、大小和哈希。
  - 读取、搜索和更新书籍元数据。
  - 删除书籍及其应用管理的文件和封面。
  - 对应用数据路径做范围校验。
  - 单个文件失败时记录 `ImportItemResult`，继续下一项。
- `BookMetadataService`：
  - EPUB 元数据和封面读取。
  - PDF 基础信息读取。
  - MOBI/AZW3 尝试基础读取，失败时允许回退到文件名。

### 3.4 Kindle 设备服务

`KindleDeviceService` 已实现：

- 扫描 Windows 可移动磁盘。
- 验证 `documents` 目录。
- 扫描 `documents` 下的 EPUB/PDF/MOBI/AZW3。
- 计算设备文件 SHA-256。
- 发送时使用临时文件扩展名 `.kkindle-part`。
- 复制完成后校验哈希，再移动为正式文件。
- 同名文件自动生成序号文件名，不覆盖已有文件。
- 删除时校验路径必须位于 `documents` 目录下。
- 提供安全弹出入口。

2026-08-05 新增 Kindle Scribe WPD/MTP 支持：

- 通过 Windows Shell 便携设备命名空间识别 Kindle（包括 Amazon VID `1949`）。
- 读取 MTP 内部存储容量与剩余空间。
- 只递归扫描 `documents`，明确跳过 `.cache`，不访问 `system`。
- 设备页显示名称、连接方式、容量及 EPUB/PDF/MOBI/AZW/AZW3/KFX 文件。
- 已用当前连接的 `Kindle Scribe` 验证：`11.4 GB 可用 / 11.9 GB`，扫描到 4 本书。
- MTP 发送已接入 Windows Shell：本机暂存为最终唯一文件名后复制，使用全新 Shell 快照轮询设备端大小；同名文件自动编号且不覆盖。
- 发送前显示目标设备和不覆盖规则，发送完成后自动刷新设备书籍；设备消失或切换时自动取消，并精确清理本次未完成文件。
- MTP 删除通过无二次系统弹窗的 `IFileOperation` 完成，只允许操作 `documents` 内选中的单个文件；应用内保留明确二次确认。
- 已用当前连接的 Kindle Scribe 对真实 EPUB 做发送、扫描、删除闭环验收，设备端无测试残留。

当前 UI 已接入 `WM_DEVICECHANGE`，并保留 3 秒一次的设备轮询作为驱动不发送事件时的可靠兜底。

### 3.5 测试

当前已有 22 个测试，并在 2026-08-06 复核通过：

- 中文 EPUB 文件名、中文元数据、封面和哈希去重。
- 标题/标签搜索。
- 单个元数据解析失败时记录逐项失败，并继续导入其他文件。
- 同一本书的同名不同内容文件使用编号文件名保留。
- 取消导入后清理 `.part`，且不写入书籍记录。
- 拒绝解析应用数据目录之外的书籍路径。
- 清理下载文件名中的 32 位哈希和 Z-Library 标记。
- 临时目录模拟 Kindle，发送和扫描书籍。
- 同名不同内容发送时生成编号文件且不覆盖原文件。
- 删除仅限 `documents` 内目标文件，且不会误删旁边文件。
- 拒绝删除 `documents` 外路径及名称以 `documents` 开头的相邻目录文件。
- SHA-256 校验失败后清理 `.kkindle-part` 和未完成目标文件。
- 取消传输后清理 `.kkindle-part` 临时文件。
- EPUB 阅读器按 spine 顺序准备章节、解析 EPUB 3 目录和片段目标。
- EPUB 解压拒绝越出 `reader-cache` 的归档路径。
- 相同卷序列号在盘符变化后仍产生相同设备身份，UI 状态和封面缓存不依赖盘符。
- 中文 EPUB 正文本地索引：按章节分块、按 `SourceHash` 判断重建，并通过全文检索找到相关片段。
- 划线/批注的新增、更新和删除持久化。
- 脚注解析只接受 EPUB 缓存目录内的 `#fragment` 目标。
- AI API Key 使用当前 Windows 用户的 DPAPI 加密保存，读取时能正确解密。

复核命令和结果：

```text
dotnet build Kkindle.sln -c Debug -p:Platform=x64 --no-restore
0 个警告，0 个错误

dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj -c Debug -p:Platform=x64 --no-restore
失败 0，通过 22，跳过 0

dotnet build Kkindle.sln -c Release -p:Platform=x64 --no-restore
0 个警告，0 个错误

dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore
失败 0，通过 22，跳过 0
```

### 3.6 最小内置阅读器

本地 `master` 在 P1 之后新增了 3 个阅读器基础提交（已随后续提交一起推送到 GitHub）：

```text
bf36d34 feat: add reader flow modes and font sizing
5c88d6a feat: add reader table of contents and controls
f841cb1 feat: add minimal built-in book reader
```

当前能力：

- 详情面板可打开 EPUB 和 PDF；其他格式会明确提示暂不支持。
- EPUB 解压到 `data/reader-cache`，严格校验归档路径，按 OPF spine 顺序组织章节。
- 支持 EPUB 3 navigation 目录、章节跳转、上一章/下一章和片段锚点。
- EPUB 支持 80%–180% 字号、白色/纸张/深色主题，以及连续滚动/横向分页。
- PDF 使用 WebView2 内置查看能力。
- WebView2 导航限制在当前 PDF 文件或 EPUB 缓存根目录内。
- 书籍详情面板新增明确的“开始阅读”按钮；原有封面右键“打开书籍”仍保留。
- 阅读界面已改为目录、正文、阅读助手三栏；目录和助手都可手动收起，并按窗口宽度自动隐藏。
- 阅读助手仅执行本地章节概览、阅读统计、复制选中文字和临时笔记；EPUB 页面脚本保持禁用，内容不上传。

## 4. 当前未完成和阻塞项

### P0：发布版 exe 启动崩溃已修复

当前发布文件：

```text
C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe
```

此前观察到：

- 启动后立即退出。
- 退出码：`-1073741189`，即 `0xC000027B`。
- Windows Application Error 指向 `Microsoft.UI.Xaml.dll`，版本 `3.2.3.0`。
- WER 记录中出现 `combase.dll` / `0x802B000A`，表现为 XAML 资源或 XAML 解析阶段失败。
- 发布目录已经确认存在 `Kkindle.pri`，因此“缺少应用 PRI”不是唯一原因。

已做过的缩小范围实验：

1. `App.OnLaunched` 直接创建一个只有 `Grid` 的窗口时，程序能保持运行。
2. `App.xaml` 去掉 `XamlControlsResources` 后，只有 `Grid` 的 `ProbeWindow` 能保持运行。
3. 加入 Button/TextBox/GridView/ListView 等控件后，当前 MainWindow 仍会触发 XAML 启动崩溃。
4. 原先直接把 `XamlControlsResources` 放进 `Application.Resources` 时也发生过崩溃。

2026-08-05 已完成并验证：

- `App.xaml` 已使用 `ResourceDictionary.MergedDictionaries` 正确合并 `XamlControlsResources`。
- `Kkindle.App.csproj` 已固定 `WindowsAppSDKSelfContained=true`。
- Release publish 成功，`Kkindle.exe` 启动 5 秒后仍保持运行。
- 发布目录存在 `Kkindle.pri`、`Microsoft.UI.pri`、`Microsoft.UI.Xaml.dll`。
- 既有 Debug 测试以 `--no-build` 运行：失败 0、通过 3、跳过 0。
- 第二轮 Release publish 已恢复灰白纸张风格主界面，启动 5 秒后仍保持运行。
- 临时 `ProbeWindow` 文件已删除。
- 第三轮 Release publish 已按用户要求改为黑白墨水屏风格、零圆角硬边矩形，启动 5 秒后仍保持运行。
- 第四轮 Release publish 已改为白色主界面和小面积黑色块，明确区分本地/Kindle 书库，并加入封面悬浮详情；启动 7 秒和双向页面切换验证通过。
- 第五轮 Release publish 已重组左侧导航为书籍管理、设备管理、阅读资料和系统；当前可用入口为电脑书库、Kindle 书籍和设备概览，未实现入口使用禁用态明确标注。
- 侧栏底部设备卡在无设备时显示“无设备连接”，连接时显示真实设备名称、连接方式、剩余/总容量，以及黑色已用、白色剩余的硬边容量条。
- 本轮因标准 publish 目录被用户正在运行的 Kkindle 占用，新版发布到同级 `publish-next`；启动 7 秒、页面导航、Scribe 名称和容量显示均验证通过。
- 第六轮 Release publish 已恢复标准发布目录，加入 MTP 安全发送、发送确认、同名自动编号和完成后刷新；启动与设备检测通过，4 个测试通过。
- 第七轮 Release publish 修复电脑书库选中后文字消失的问题；启动自动发现 Kindle 并询问是否连接，接受后显示设备与容量。
- 侧栏设备名右侧增加 `▲` 弹出按钮：USB 磁盘调用安全弹出接口，MTP 让 Kkindle 停止访问并提示可以断开 USB；传输进行中禁止弹出。
- “暂不连接”在本次物理连接期间不会重复提示，设备拔出重连或手动刷新后可再次选择。
- 第八轮 Release publish 统一覆盖 WinUI 普通按钮、强调按钮和 ContentDialog 的黑白 Normal/Hover/Pressed 状态，连接弹窗改为白底黑框、零圆角且不再出现蓝色按钮。
- 电脑书库自定义文字和计数显式跟随悬停反色；弹出按钮改用模板绘制的实心三角形，移入为白底黑三角，移出为黑底白三角。
- 第九轮 Release publish 不再使用 WinUI `ContentDialog` 显示连接与弹出确认，改为主窗口内完全自绘的黑白模态层，彻底移除系统圆角和蓝色按钮模板。
- 自绘设备确认支持连接/暂不连接、断开/取消以及 Enter/Esc；启动连接和弹出取消流程均通过 UI Automation 验证。

最值得优先验证的资源写法：

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <controls:XamlControlsResources />
        </ResourceDictionary.MergedDictionaries>

        <!-- 自定义资源放在这里 -->
    </ResourceDictionary>
</Application.Resources>
```

需要在 `Application` 或 `ResourceDictionary` 上声明：

```xml
xmlns:controls="using:Microsoft.UI.Xaml.Controls"
```

不要把 `XamlControlsResources` 当成普通资源字典项随意设置 `x:Key`，除非编译器明确要求且已验证运行时行为。

### P0 后续收尾顺序

建议后续 AI 按以下顺序，不要一开始同时改大量业务代码：

1. 保留 `Kkindle.pri` 发布复制 Target 和自包含发布配置。
2. 删除 `ProbeWindow.xaml`、`ProbeWindow.xaml.cs` 和无用的调试代码。
3. 恢复完整 MainWindow 时分批加入控件，每次发布后做至少 5 秒存活检查。
4. 按用户要求，每生成一次 exe 后创建一次对应的 Git 提交。

### 当前 UI 注意事项

- `MainWindow.xaml` 已恢复书架和设备书库主界面，但筛选、分类与设置仍有占位入口。
- 书架卡片已显示封面和元数据，并提供封面悬浮详情；仍需用长标题和高 DPI 验收布局。
- `MainWindow.xaml.cs` 已包含不少交互入口，但要以当前 XAML 的 `x:Name` 和事件绑定为准，避免恢复 UI 时出现名称不匹配。
- 当前没有稳定的页面导航框架；首版可以先保持单窗口 + 详情面板。
- `ProbeWindow.xaml` / `ProbeWindow.xaml.cs` 已删除，不要恢复临时诊断入口。
- `App.xaml` 已通过合并字典稳定加载 `XamlControlsResources`，并包含阅读器工具栏与目录选中样式；不要改变现有资源合并层级。
- PowerShell/终端显示中文时曾出现乱码样式；继续编辑源码请使用 UTF-8，不能根据终端显示的乱码直接判断业务字符串是否损坏。当前 Debug build 已通过。

## 5. 发布与启动验证

从项目根目录运行：

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -c Debug -p:Platform=x64
dotnet test Kkindle.sln -c Debug -p:Platform=x64 --no-build

dotnet publish src\Kkindle.App\Kkindle.App.csproj `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  --self-contained true `
  -p:WindowsAppSDKSelfContained=true
```

发布目录：

```text
src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

当前应使用的最新程序：

```text
C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe
```

`publish-fix` 是第十一轮启动修复时的临时发布目录；第十三轮已成功恢复覆盖标准 `publish` 目录，后续不要再把 `publish-fix` 当成最新版。

启动验证建议：

```powershell
$exe = "C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe"
$process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
Get-Process -Id $process.Id -ErrorAction SilentlyContinue
Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
```

当前不能把“发布成功”当成“exe 可用”：必须确认进程启动后仍存在，最好再做窗口截图或手工点击验收。

## 6. 下一阶段任务清单

### P0 启动稳定性

- [x] 修复 WinUI XAML 资源 / 发布包启动崩溃。
- [x] 让发布版 exe 启动后稳定保持运行至少 5 秒。
- [x] 确认 `Kkindle.pri`、`Microsoft.UI.pri` 和 WinUI 依赖都随发布输出存在。
- [x] 将 `WindowsAppSDKSelfContained=true` 写入 `Kkindle.App.csproj`，避免只依赖命令行参数。
- [x] 删除临时 Probe 文件和调试代码。
- [x] 将原生标题栏替换为可启动、可拖动、可缩放的全自绘黑白矩形标题栏。
- [x] 验证 DWM 最终圆角偏好为 `DWMWCP_DONOTROUND`，避免 presenter 初始化后恢复系统圆角。

### P1 恢复可用 UI

- [x] 恢复黑白墨水屏风格、零圆角硬边矩形的完整 MainWindow。
- [x] 恢复书架卡片封面、标题、作者和格式信息。
- [x] 区分本地书库与 Kindle 书库，并加入封面悬浮详情。
- [x] 按书籍管理、设备管理、阅读资料和系统重组左侧导航，并加入设备连接与容量状态卡。
- [x] 恢复搜索、列表/网格切换和书籍详情面板。
- [x] 验证空书库、无封面、长标题和高 DPI 布局。
- [x] 用真实导入的 EPUB 做一次完整手工验收（元数据、封面、长标题省略）。
- [x] 完成作者、标签和格式筛选，并提供无结果状态与清除筛选。

### P1 完善 Kindle 流程

- [x] 设备页显示 USB 磁盘与 WPD/MTP Kindle 的设备书籍和容量。
- [x] 发送前显示目标设备和同名不覆盖规则。
- [x] 发送完成后刷新设备书籍列表。
- [x] 只删除 `documents` 下目标文件，并完善二次确认。
- [x] 设备消失/切换时取消传输，失败与取消均清理本次临时文件；传统磁盘仍以卷序列号识别盘符变化。
- [x] 接入 WM_DEVICECHANGE，并保留轮询作为可靠兜底。
- [x] 用真实 Kindle Scribe 完成 EPUB 发送、扫描和删除闭环验收。

### P2 补充测试

- [x] 损坏文件逐项失败且不影响其他文件。
- [x] 中文文件名和中文元数据。
- [x] 文件名清理和同名冲突。
- [x] SHA-256 校验失败后的临时文件清理。
- [x] 取消导入和取消发送。
- [x] 同名不同内容发送到 Kindle 时自动编号。
- [x] `documents` 路径安全边界。
- [x] 设备盘符变化后的卷序列号识别。
- [x] 真实 Kindle Scribe 大文件传输：64 MiB EPUB 发送、设备端大小校验和删除闭环。
- [x] MTP 断开确认与停止访问流程。
- [ ] 真实 Kindle 物理拔出/重连事件验收（需要人工操作 USB 线）。
- [ ] USB 磁盘型 Kindle 的系统安全弹出验收（当前 Scribe 为 MTP，不适用磁盘弹出接口）。

## 7. 不要做的事情

- 不要读取或修改 calibre 数据库。
- 不要访问 Kindle 的 `system` 目录或内部数据库。
- 不要覆盖 Kindle 上内容不同的同名文件。
- 不要因为修 UI 而改变 SQLite 数据结构，除非先更新迁移策略。
- 不要把单个导入失败升级为整批失败。
- 不要破坏当前“窗口激活后再配置原生 chrome、布局完成后重申 DWM 直角”的初始化顺序。
- 不要在没有明确需求时继续扩大当前最小阅读器范围；格式转换、阅读进度同步、笔记和云同步仍属于范围外功能。

## 8. Git 状态

当前工作分支为 `master`；`origin/master` 最新为 `4e8009b`，本地领先 18 个提交，未自动推送。当前工作区另有未提交的 `src/Kkindle.App/App.xaml` 修改和未跟踪的 `.opencode/` 目录，均不是本轮目录定位修复的一部分，必须保留。构建输出由 `.gitignore` 排除，不纳入版本控制。

当前开发基线及最近提交（本轮最终提交位于最上方）：

```text
fix: normalize reader chapter start  ← 本轮最终提交（目录跳转首行归一化：根因是 EPUB 首元素自带上边距/首行空白节点，scrollTop=0 无法消除；新增 ShouldNormalizeChapterStart 策略 + 章节开头安全归一化脚本——删除首行空白块、把第一个有效内容元素 margin-top 以 inline !important 归零，保留段落间距/标题层级/图片封面 fit；带 fragment 或搜索/书签/批注/AI 目标不归一化）
fix: reset reader position on toc navigation  ← 上轮最终提交（目录跳转定位修复：ReaderNavigationIntent 明确导航意图 + 导航前清理旧 pending 定位 + 目录/进度条跳转显式复位到章节首行/分页首列 + 带 fragment 锚点仍跳锚点 + 同章节点击复位并重对齐滚动边沿）
fix: reduce reader chapter switch jank  ← 上上轮最终提交（章节切换闪现/卡顿：导航期间保持旧内容可见 + 首屏准备完成后短淡入/滑入 + 非首屏任务延迟并带导航序列守卫 + 跳转一律短淡入）
fix: smooth reader chapter transitions  ← 上上上轮最终提交（X 卡死修复：钩子跨线程锁死/同步等待 WebView 根因 + 幂等非阻塞关闭/有界落库 + 章节平滑过渡统一收敛/默认仿真）
fix: fit epub images to reader viewport  ← 上上上上轮最终提交（EPUB 图片/封面按正文视口自适应：分页 contain 拟合 + 按尺寸识别封面 + 滚动模式防横向溢出）
fix: restore reader pagination layout  ← 上上上上上轮最终提交（分页正文排版修复/列宽对齐/列吸附/排版数据安全回退）
feat: expand reader productivity tools
c91972b fix: repair reader page interaction
37e2057 docs: refresh handoff timestamp
d621d68 docs: record viewport fix commit hash
1444cb9 fix: fit reader content to viewport
5d875af feat: add reader zen mode and page navigation
60b5683 fix: refine reader header controls
e594863 fix: align reader header separators
46c14d7 fix: show book details on click
bb5e9c4 fix: add hover border to book cards
3d3c3ec fix: emphasize selected book card border
4e8009b feat: add reader AI assistant, highlights, notes, and book index
```

继续工作前建议：

```powershell
git status --short --branch
```

继续遵循“一次 exe 发布对应一次 Git 提交”，便于后续回退和比较。

## 9. 第十轮发布（2026-08-05）

- 主窗口启用自定义标题栏：白底、黑色分隔线、黑底白字 K 标识，窗口标题统一为 `Kkindle`。
- 原生最小化、最大化和关闭按钮保留，标题栏按钮使用黑白悬停/按下配色。
- 通过 DWM 设置 `DWMWCP_DONOTROUND`，禁用 Windows 11 外层窗口圆角，并指定黑色窗口边框。
- 新增多尺寸 `Assets/Kkindle.ico`，采用与侧栏一致的黑底白 K；图标已嵌入 exe，并复制到发布目录供运行时窗口/任务栏使用。
- Release x64 自包含发布成功；标准发布目录中的 `Kkindle.exe` 已更新。
- Release 测试通过：失败 0、通过 4、跳过 0。
- 自动化环境中程序进程可保持运行，但本轮工具会话无法枚举 WinUI 主窗口句柄，因此标题栏和外框仍应在交互桌面上做一次肉眼验收。

## 10. 第十一轮发布：启动修复（2026-08-05）

- 修复第十轮自定义窗口样式在窗口激活前访问 `AppWindow`，导致部分机器出现“进程存在但主窗口不显示”的问题。
- XAML 自定义标题栏仍在构造阶段设置；原生标题栏颜色、运行时图标和 DWM 直角设置延迟到首次 `Window.Activated` 后执行。
- 原生窗口装饰设置增加失败隔离：即使某项系统 API 不受支持，也不会阻止主窗口打开。
- 由于两个旧版 Kkindle 进程锁定标准发布目录，本轮修复版发布到同级 `publish-fix` 目录。
- 修复版实际启动验证通过：2 秒内取得主窗口句柄，窗口标题为 `Kkindle`，进程可响应。

## 11. 第十二、十三轮发布：全自绘窗口按钮与真正直角（2026-08-05）

- 使用 `OverlappedPresenter.SetBorderAndTitleBar(true, false)` 移除 Windows 原生标题栏按钮，保留系统窗口缩放边框。
- 标题栏右侧新增三个完全自绘的矩形按钮：最小化、最大化/还原、关闭；Normal、Hover、Pressed 均为黑白直角样式。
- 标题栏拖动区域与按钮区域分离，按钮可正常交互；最大化状态下图标和辅助功能名称会切换为“还原”。
- 修复 presenter 切换完成后 Windows 重置圆角偏好的时序问题：在低优先级调度、首次布局和 presenter 状态变化后重复应用 DWM 直角设置。
- 最终启动验证：窗口标题 `Kkindle`、进程可响应，三个自绘按钮均可由 UI Automation 识别。
- DWM 验证返回成功且 `CornerPreference = 1 (DWMWCP_DONOTROUND)`；离屏窗口截图确认外框为真正直角。

## 12. 当前继续开发基线

- 标准 `publish` 目录中的 exe 已更新为三栏阅读界面版本，并通过真实 EPUB 与阅读助手验收。
- `MainWindow.ConfigureTitleBar()` 只负责 XAML 标题栏和拖动区域，不应在窗口激活前读取 `AppWindow`。
- `ConfigureNativeWindowChrome()` 只能在首次 `Window.Activated` 后调用；其中隐藏原生标题栏并取得 `OverlappedPresenter`。
- `ApplySquareWindowFrame()` 必须保留首次调用、低优先级调度调用、`Loaded` 调用和 presenter 状态变化后的调用，否则 Windows 可能重新恢复圆角。
- 自绘最小化、最大化/还原、关闭按钮位于 `MainWindow.xaml`；统一模板 `TitleBarCaptionButtonStyle` 位于 `App.xaml`。
- 阅读状态下 `ReaderPane` 必须保持全窗口覆盖、`Canvas.ZIndex="40"`；`WindowChromeLayer` 保持 `Canvas.ZIndex="50"`。目录、正文和助手面板各自使用 `Margin="0,38,0,0"` 为自绘标题栏让位，避免露出旧书架 Logo/标题。
- 目录栏宽度为 286 logical px，助手栏宽度为 310 logical px；窗口宽度低于 1180 logical px 自动隐藏助手，低于 760 logical px 自动隐藏目录。
- `ConfigureReaderWebView()` 继续保持 `IsScriptEnabled = false`。阅读助手通过应用主动执行的只读脚本提取当前 EPUB 片段，不能为了助手功能直接启用 EPUB 自带脚本。
- 正文阅读区视口规则（1444cb9 + 本轮修复）：WebView 宿主本身就是正文视口，宽高由目录/助手/窗口布局决定；滚动模式正文自然增长并在 WebView 内纵向滚动；分页模式用 CSS 多列按视口分页，`html { overflow: hidden }` 是唯一滚动容器，`body` 必须保持 `overflow: visible`，否则列溢出会被传播到视口裁切，出现"整章压进一屏"。
- 分页列宽规则（本轮修复，必读）：分页列宽 + 列间距必须严格等于视口宽，否则翻页按 `window.innerWidth` 推进时与列边界错位，累计漂移会把屏幕切成多个半列并出现大空白、左侧裁切。正确写法是 `body { column-width: calc(100vw - 48px); column-gap: 48px; column-fill: auto; padding: 48px 24px 64px; writing-mode: horizontal-tb !important; }`（24px 左右内边距各形成一页的左右留白，`column-width + column-gap == 100vw`）。分页正文必须显式 `writing-mode: horizontal-tb !important`，防止 EPUB 自带的竖排规则污染默认横排；竖排只允许在滚动模式下由 `VerticalWriting` 开关显式启用。分页翻页/恢复/目录/搜索/书签/批注/注释跳转后统一调用 `SnapReaderPaginationAsync()`（把 `scrollLeft` 吸附到 `paddingLeft + N × 视口宽` 的列边界并钳制到最大范围、固定 `top:0`）。
- 阅读区尺寸变化（窗口缩放、目录/助手收起、禅模式切换）会触发 `ReaderContentPanel_SizeChanged` → `ScheduleReaderRelayout()`（防抖 120 ms）重新应用视口样式并收敛滚动位置；分页翻页脚本固定 `top: 0`，防止纵向漂移。
- P1 已完成，P2 自动化场景已增至 18 项，Kindle Scribe 64 MiB 传输闭环也已通过。下一步只剩需要人工配合的物理拔出/重连；USB 磁盘安全弹出需等对应设备。不要再次重做标题栏架构，除非有新的可复现问题。
- 本轮新增阅读工具基线（详见第 23 节）：每本书独立的排版设置（字号/行高/正文宽度/左右边距/CJK 字体/竖排）持久化到 SQLite `ReaderLayoutSettings`；阅读进度断点保存到 `ReaderProgress`（按 BookFile 一行，含章节路径/fragment/滚动位置/百分比/流动模式，滚动与分页/竖排按轴区分，避免把分页位置用在滚动模式）；书签存 `ReaderBookmarks`（工具栏按钮 + Ctrl+B，目录面板新增“目录/书签”标签页）；整本书搜索复用 `BookContentChunks` 本地 FTS（失败回退 LIKE），入口为工具栏“搜索”按钮和 Ctrl+F，面板用 Popup 浮于 WebView2 之上；选中文字会弹出黑白快捷工具条（复制/划线/批注/AI 解释/搜索），因 `IsScriptEnabled=false` 冻结页面事件，使用宿主侧 300ms 轮询读取选区矩形再换算屏幕坐标定位；划线/批注可在“划线与笔记”页导出 Markdown 与纯文本（`ReaderAnnotationExport`，走 FileSavePicker）；阅读统计累计“活动且可见”的阅读秒数（窗口激活 + 阅读面板可见，每秒累计、每 30 秒落库、关闭时强制落库）到 `ReaderReadingStats`，目录面板显示已读章节/全书百分比/累计与本次时长；CJK 增强包括可选的 CJK 字体覆盖、`line-break: strict` + `word-break: normal` + 两端对齐的严格断行/标点规则、ruby/furigana 居中显示，以及滚动模式下的 `writing-mode: vertical-rl` 竖排（分页模式竖排不生效，有提示）。
- 本轮排查并修复一个必须记住的坑：`Slider`/`ComboBox` 的 `ValueChanged`/`SelectionChanged` 事件会在 XAML 解析过程中（给 `Minimum`/`Maximum`/`Value` 赋值时）提前触发，此时兄弟控件尚未创建；任何在该事件里访问其他 XAML 控件的事件处理器都会抛空引用，并被包装成 `XamlParseException: Failed to assign to property 'RangeBase.Minimum/Value'` 的启动崩溃。修复方式是 `ReaderLayoutSettingChanged`/`ReaderFontFamilyBox_SelectionChanged` 统一加 `AreReaderLayoutControlsReady()` 空值守卫。
- 本轮新增 EPUB 图片/封面基线（详见第 25 节，必读）：分页模式对 `img`/`svg` 强制 `width:auto !important; height:auto !important; max-width:100% !important`，并用实测内容盒高度 CSS 变量 `--kkindle-page-content-h`（注入脚本读 `body.clientHeight - paddingTop - paddingBottom` 写到 `documentElement`）做 `max-height: calc(var(--kkindle-page-content-h) - 3.6em)` 的 contain 拟合（3.6em 即图片自身 1.8em 上下外边距），保证图片+边距整体不超当前页内容盒、不被底部裁切、不变形，且基于真实 WebView viewport/内容盒而非整个窗口；首个大图（`naturalWidth/naturalHeight` 未解码时回退 `width/height` 属性，阈值：面积 ≥ 视口 35% 或宽高分别 ≥ 60% 视口，与书名/文件名无关）加 `.kkindle-cover` 类收紧到 `- 6em` 并把外边距缩为 1em，让封面与标题同页；滚动模式只保留 `height:auto !important; max-width:100% !important`（不强制 `width`，保留 EPUB 自身百分比宽度如本书 `div.chatu-part img{width:40%}` 装饰图），自然高度不压缩、无横向溢出。图片适配在 `ApplyReaderAppearanceAsync()` 内执行，`NavigationCompleted` 后经 `RetryReaderImageFitAsync()` 在 250ms/950ms 主机侧重试（图片延迟解码后补加封面类并重新吸附）；窗口缩放/收目录/收助手/禅模式/字号排版变化仍经 `ScheduleReaderRelayout()` 自动重适配。`IsScriptEnabled=false` 与导航白名单不变。
- 本轮新增关闭卡死与章节过渡基线（详见第 26 节，必读）：**低级鼠标钩子回调严禁触碰 XAML**——钩子线程只读 `_readerHookEnabled` + 缓存屏幕矩形 `_readerWebViewScreenRect`，点击经 `DispatcherQueue.TryEnqueue` 投递 UI 线程；否则 `UnhookWindowsHookEx` 会与在途回调的跨线程 XAML 访问双向死锁，用户点 X 时整窗卡死。**关闭流程必须幂等且不阻塞 UI 线程**：`CloseReader()`/`MainWindow_Closed()` 先停钩子/轮询/计时器/取消动画与重排令牌、关全部 Popup，再发起有界（1500ms `WaitAsync`）异步落库，`skipWebViewCapture=true` 时用 `_readerLastProgress` 而不调 `ExecuteScriptAsync`（WebView 导航/关闭中脚本可能永不返回）；绝不在 UI 线程 `.Wait()/.Result`。进度/统计保存失败不能阻止关闭。所有真实章节切换路径收敛到 `ShowReaderChapterAsync`/`NavigateReaderSourceAsync`：默认动画改为“仿真”（淡入淡出+轻微缩放），左右滑动按方向平移，无动画保持立即切换；过渡用既有宿主变换（`ReaderWebViewHost` Opacity/Scale/TranslateX）播放 130ms 出/190ms 入，带取消令牌与 3 秒看门狗，`_readerTransitionActive` 在过渡期间抑制滚动轮询/分区点击，`NavigationCompleted` 之后释放；关闭第一时间取消动画并复位变换，动画与关闭互不等待。
- 本轮新增章节切换时序与首屏拆分基线（详见第 27 节，必读）：**不要在导航前把唯一 WebView 淡出/滑出**。导航期间当前章节保持完全可见；`NavigationCompleted` 后先隐藏宿主（`ReaderWebViewHost.Opacity=0`，白底而非黑屏）做首屏准备（排版 CSS/视口/图片适配/目标位置恢复/分页吸附/边沿校准），首屏就绪后播放 180ms 淡入或滑入，无动画立即显示；入动画时长 180ms 落在 120–220ms 目标区间。**首屏/非首屏必须拆分**：批注还原、脚注 hover、进度/统计刷新等非首屏工作延迟到 `RunReaderPostNavigationWorkAsync`（fire-and-forget），每个 await 后检查 `IsStaleReaderNavigation(sequence, token)`（序列号 `_readerChapterTransitionSequence` + `_readerChapterTransitionCancellation` + `_readerCloseRequested`），旧章节/关闭的后置任务立即中止，绝不允许覆盖新章节或卡住关闭。**导航序列守卫**：`_readerPendingNavigationTarget` 只允许最后一次请求的导航走首屏/入场/后置流程；被取消/取代的旧导航 `NavigationCompleted`（`IsSuccess=false`）不提前释放过渡守卫；守卫由最新章节成功完成或 3 秒看门狗释放，且每次导航（含无动画）都武装看门狗。**跳转一律短淡入**：目录/搜索/书签/批注/AI 来源/进度条跳转传 `jump: true`，即使选“左右滑动”也播淡入，避免远章节跳转看起来像逐页拖动；上一/下一章、滚动接章、分页跨章保持滑动/仿真。未做双 WebView 相邻章节预加载（避免新架构引入卡死风险）。

## 13. P1 完成发布（2026-08-05）

- 本地书库支持作者、标签和格式筛选，支持清除筛选及无结果空状态。
- 已验收空书库、无封面占位、长标题省略和 120 DPI（125%）布局。
- 已用真实 EPUB 验收标题、作者、封面解析和书架显示。
- Kindle 设备页支持选中书籍后安全删除，应用确认信息明确显示目标位于 `documents`。
- WPD/MTP 使用无二次系统弹窗的 `IFileOperation` 删除，避免后台 Shell 删除确认导致界面卡住。
- MTP 发送改为本地暂存最终唯一文件名后复制；轮询使用全新 Shell 快照，解决设备目录缓存导致的超时。
- 设备断开或切换会取消传输；失败与取消会精确清理本次未完成的设备文件。
- `WM_DEVICECHANGE` 已接入，3 秒轮询继续作为可靠兜底。
- 真实 Kindle Scribe 验收结果：发送成功、设备扫描可见、删除成功、测试残留为 0。
- Release 测试结果：失败 0、通过 7、跳过 0；发布版启动和筛选控件 UI Automation 验证通过。

## 14. 最小内置阅读器（2026-08-05）

- 新增 EPUB/PDF 阅读入口和主窗口内阅读面板。
- EPUB 支持安全解压、spine 章节顺序、EPUB 3 目录、章节与锚点跳转。
- 新增上一章/下一章、目录、80%–180% 字号、三种主题和连续滚动/横向分页。
- 阅读内容导航限制在当前 EPUB 缓存目录或当前 PDF 文件，阻止跳转到外部路径和网络地址。
- 新增 3 项阅读器测试；本地提交为 `f841cb1`、`5c88d6a`、`bf36d34`，尚未推送到 `origin/master`。

## 15. P2 自动化测试补强（2026-08-06）

- 新增 8 项测试，总数从 10 增至 18。
- 覆盖单项导入失败隔离、同名文件编号、取消导入清理、应用数据路径越界、Kindle 相邻目录越界和 SHA-256 失败清理。
- 加强中文 EPUB 验证，确认中文文件名、标题、作者、简介和实际保存文件名均正确。
- 修复下载文件名清理顺序：32 位哈希位于 `(Z-Library)` 标记之前时也能被移除。
- 统一设备身份为 `KindleDevice.Identity`：优先使用卷序列号，盘符变化不会触发新设备提示或破坏封面缓存身份。
- Debug/Release x64 完整解决方案构建均通过：0 警告、0 错误；两个配置的测试均为失败 0、通过 18、跳过 0。
- 真机为 `Kindle Scribe`（MTP，11.4 GB 可用 / 11.9 GB）；唯一测试文件大小为 67,110,066 字节，发送完成后通过设备端大小校验并成功删除。
- 删除后再次只读枚举 `documents`，测试文件残留为 0；本机 64 MiB 临时 EPUB 和临时测试工具也已清理。
- 本轮没有重新发布 EXE；标准 `publish` 目录仍是上一轮阅读器基线。

## 16. 阅读界面重设计发布（2026-08-06）

- 按用户提供的桌面阅读器截图重做主阅读区，采用左侧目录、中间正文、右侧本地阅读助手的三栏结构。
- 左栏显示封面、书名、作者、格式、阅读进度、目录搜索和章节列表；中栏使用紧凑顶部工具栏、留白阅读画布与底部翻页/章节滑块；右栏明确标注“本地工具 · 内容不上传”。
- 目录和助手支持手动折叠；窗口宽度低于 1180 logical px 自动隐藏助手，低于 760 logical px 自动隐藏目录。
- EPUB 正文样式已优化正文宽度、行高、标题、引用、图片和表格；底部 Slider 已覆盖为黑灰色轨道与滑块，不再出现系统蓝色。
- 修复阅读层只从标题栏下方开始导致旧书架 Logo/标题露出的问题：阅读层覆盖整个窗口，三个内容面板单独为 38px 自绘标题栏让位。
- 书籍详情面板新增“开始阅读”主按钮，解决阅读入口只存在于封面右键菜单、可发现性不足的问题。
- 阅读助手支持章节概览、阅读统计、复制选中文字、临时笔记和清空面板。章节概览/统计会按当前 EPUB URL 片段提取到下一个同级或更高级标题，避免把整本合并 XHTML 误算为当前章节。
- `WebView2.Settings.IsScriptEnabled` 仍为 `false`；助手只运行应用自身的只读提取脚本，没有启用 EPUB 内嵌脚本。
- Debug/Release x64 完整构建均为 0 警告、0 错误；Debug/Release 测试均为失败 0、通过 18、跳过 0。
- 标准 Release 便携版已发布并启动存活至少 5 秒；真实 EPUB 已验证“开始阅读”、目录跳转、黑灰进度条、章节概览、阅读统计、临时笔记、清空面板和响应式侧栏。
- 最终完整 DPI 截图：`C:\Users\kings\.codex\visualizations\2026\08\06\019fd520-bebc-7740-84de-ac82ef43a5f4\reader-final-release.png`。
- 发布文件：`C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`。
- 本轮对应提交信息：`feat: redesign reader workspace`；不推送。

## 17. 阅读助手 AI 与划线批注（2026-08-06）

- 阅读助手改为右侧浮动面板（Popup，宽 360，从 38px 自绘标题栏下方开始），内部提供“AI”和“笔记”两个标签页；窗口过窄时仍随响应式布局隐藏。
- AI 标签页支持 DeepSeek（默认）、OpenAI 和自定义（OpenAI 兼容 Chat Completions）三种服务；设置面板可配置 Provider、Base URL、模型和 API Key。
- API Key 通过 Windows DPAPI（当前用户）加密后保存到 `data/ai-settings.json`，不落明文。
- 对话支持 Ctrl+Enter 发送、当前章节总结、解释选中文字、全书结构化概览和清空对话；请求超时 120 秒，携带最近 8 轮上下文，单轮内容限制 3500 字符。
- 打开 EPUB 后自动建立本地全文索引：`EpubBookContentService` 按章节把正文切分为约 1000 字符片段（最小 620、重叠 160），写入 SQLite `BookContentChunks`，并按 `BookFileId + SourceHash` 判断是否需要重建。
- 提问时在本地检索最多 7 个相关片段作为引用上下文；全书概览抽取 12 个章节开头片段。检索优先 FTS，异常时回退 LIKE。
- 回答下方显示“本次参考 [n]”来源按钮，点击可跳转到对应章节并滚动到片段起始位置。
- 工具栏新增“划线”和“批注”：划线直接保存黑色高亮；批注打开笔记页填写文字。数据持久化到 SQLite `ReaderAnnotations` 表（章节路径、片段、起止偏移、选中文字、前后缀锚点、颜色和笔记）。
- 同一位置已有划线时再次保存会合并笔记而不产生重叠划线；与既有划线重叠的选区会被拒绝。
- 笔记页列出当前书的全部划线/批注，支持选中和删除；划线会在正文重新加载时通过只读脚本还原。
- 新增 `EpubFootnoteResolver`：只解析 EPUB 缓存目录内、且以 `#fragment` 定位的脚注目标，XDocument 解析失败时安全跳过，单次最多 120 个目标，文本截断到 1200 字符。
- 测试从 18 增至 22，新增：中文 EPUB 正文索引与相关片段检索、划线/批注增删改持久化、脚注目录边界安全、AI API Key DPAPI 加密。Debug/Release 构建 0 警告 0 错误，两个配置测试全部通过。
- 本轮代码已提交并推送到 GitHub：`4e8009b feat: add reader AI assistant, highlights, notes, and book index`。
- 说明：AI 对话需要用户自行配置有效 API Key；真实服务调用尚未在本文档记录人工验收，应先完成配置后手工验证一次完整问答。

## 18. AI 功能发布（2026-08-06）

- 标准 `publish` 目录已用当前 `master` 重新发布，`Kkindle.exe` 更新于 2026-08-06 15:16，包含第 17 节的 AI 问答、划线/批注和本地全文索引。
- 发布前 Debug/Release x64 构建均为 0 警告、0 错误，22 项测试全部通过。
- 发布后启动验证：进程 5 秒后仍存活，主窗口标题为 `Kkindle`，主窗口句柄正常。
- 功能代码已包含在已推送的 `4e8009b` 中，本次发布不新增代码提交，仅记录发布轮次。

## 19. Kreader 阅读器标识（2026-08-06）

- 打开书籍进入阅读器后，顶部自绘标题栏左端显示黑色 `Kreader` 字样（FontSize 18、Bold、CharacterSpacing 60，与侧栏 Kkindle 品牌风格一致）；关闭阅读器后自动隐藏。
- 该字样设置 `IsHitTestVisible=False`，不遮挡标题栏拖动区域。
- XAML 位于 `WindowChromeLayer`（ZIndex 50）内，位于阅读器内容之上；显示/隐藏由 `MainWindow.xaml.cs` 在打开/关闭阅读器时切换。
- Debug/Release x64 构建均为 0 警告、0 错误，22 项测试全部通过；标准 `publish` 目录已重新发布（exe 更新于 15:20），启动验证通过。

## 20. 阅读器顶部对齐与分隔线精简（2026-08-06）

- 统一顶部三栏头部行高：正文工具栏从 54 改为 52，与目录、AI 助手头部（52）对齐，使三栏头部的按钮和分隔线处于同一水平线。
- 统一底部行高：目录面板"返回书架"行从 46 改为 50，与正文底栏（50）对齐，底部按钮位于同一水平线。
- 去掉与"目录"标题线同高的两根分隔线：正文工具栏底部边框、AI 助手头部底部边框。
- 去掉 AI 助手右侧面板最上方的遗留分隔线：AI 对话/划线与笔记标签行底部边框。
- 保留目录标题线；正文阅读画布黑色外框、AI 助手选中标签的黑色底色不受影响。
- Debug/Release x64 构建均为 0 警告、0 错误，22 项测试全部通过；标准 `publish` 目录已重新发布并验证。

## 21. 正文视口自适应修复（2026-08-07）

### 根因

打开真实 EPUB 后，分页模式正文会"一次显示整章"：页面把整章内容压缩/裁切在一个视口内，无法在章节内翻页。

定位到根因是分页模式的注入 CSS 组合错误：

```css
/* 修复前 */
html, body { height: 100%; overflow-y: hidden !important; }
body { column-width: calc(100vw - 144px); column-gap: 144px; column-fill: auto; ... }
```

- `body` 的多列布局实际是生效的：实测 `document.body.scrollWidth` 达到 539996 px（约 765 页），但 `document.documentElement.scrollWidth` 只有 842 px。
- 根因是 `html` 上的 `overflow-y: hidden` 被按规范传播给视口，`html` 自身不再作为滚动容器，`body` 的水平列溢出被困在 `body` 内部、被视口裁切，用户看不到后续页，`document.scrollingElement.scrollWidth` 也为 0 溢出，导致翻页逻辑判定"章节内不可翻页"，上一页/下一页会直接跨章。

### 本次改动

1. `src/Kkindle.App/MainWindow.xaml.cs` — `ApplyReaderAppearanceAsync()` 分页模式 CSS 修复：
   ```css
   html { height: 100%; overflow: hidden !important; }
   body { height: 100%; overflow: visible !important; padding: 48px 24px 64px !important;
          box-sizing: border-box; column-width: calc(100vw - 96px); column-gap: 48px;
          column-fill: auto; max-width: none !important; }
   ```
   `html` 成为唯一滚动容器，列溢出落在 `document.scrollingElement` 上，每页宽 ≈ 一视口宽；分页逻辑、鼠标分区点击、键盘左右键和左右滑动动画全部沿用原实现。
2. `MainWindow.xaml.cs` — 分页翻页脚本（`TryTurnWithinChapterAsync` 分页分支）与 `MoveReaderToEndAsync` 分页分支改为 `window.scrollTo({ left, top: 0 })`，锁定纵向位置，防止分页模式纵向漂移。
3. `MainWindow.xaml.cs` — `ApplyReaderAppearanceAsync()` 分页模式追加 `top: 0` 归位：章节加载/字号变化后正文始终停在当前列顶部。
4. `MainWindow.xaml.cs` — 新增防抖重适配：`ReaderContentPanel_SizeChanged` → `ScheduleReaderRelayout()`（120 ms 防抖）→ `ApplyReaderAppearanceAsync()` + `ClampReaderScrollAsync()`，窗口缩放、目录/助手收起、禅模式切换后正文按新视口重新分页并收敛滚动位置；取消令牌在 `CloseReader()` 与 `MainWindow_Closed()` 释放。
5. 未改 `WebView2.Settings.IsScriptEnabled=false`（`ConfigureReaderWebView()`），未改导航白名单与标题栏，未提交既有未提交的 `App.xaml` 改动。

### 已验证项目（真实 EPUB《策略思维》，Release/Debug 实测）

- 启动并打开真实 EPUB：正文宿主 `843x637`，WebView 视口 `842x636`，正文只在视口内显示。
- 滚动模式：长章节（92 KB XHTML）`scrollHeight` 285726 px，在视口内纵向滚动；`TurnReaderPageAsync` 翻页返回 `True` 并推进滚动。
- 分页模式：长章节 `document.scrollingElement.scrollWidth` 443921 px（约 765 页），`clientHeight` 636；翻页 `scrollLeft` 按 842 px（一视口宽）推进（实测 `sl: 0 → 842.4 → 1684.8`），纵向 `st` 保持 0；章节边界推进/返回的既有逻辑未破坏（`TurnReaderPageAsync` 在边界处正确返回/跨章）。
- 布局变化重新适配：收起目录后宿主 `843 → 1203`；窗口缩放后宿主 `267x389 / 537x475 / 843x637`，WebView 视口与宿主完全一致（`iw/ih` 对齐），分页总数随视口重新计算（`scrollWidth` 443921 → 667042 → 545088）。
- 禅模式：进入后正文区域扩展（`537x475 → 1490x739`），视口随之对齐。
- 目录跳转、返回书架、字号缩放控件仍可用；`git diff` 仅含 `MainWindow.xaml.cs`（本次）与 `App.xaml`（既有未提交）。

### 未验证/风险

- 自动化环境无法把真实鼠标/键盘事件可靠投递到 WebView2 合成渲染内容（WebView HWND 为 0x0 的 composition island，点击坐标落入桌面），因此"分页模式左 1/3 上一页/右侧下一页、左右键翻页"的端到端输入路径未在自动化中触发；已通过程序化调用同一生产代码路径（`TurnReaderPageAsync` → 章节内翻页脚本）验证滚动位置确实按一视口推进。建议人工在真机上各点一次确认。
- 禅模式退出：浮层"退出禅模式"按钮在 UIA 树与像素扫描中均不可达，未自动化点击；退出走与进入对称的 `ApplyReaderZenLayout()` 恢复路径，未单独自动化验证。
- 分页模式若遇超高单元素（大图等）最后一列可能有少量纵向溢出，翻页时 `top:0` 会归位，不影响阅读。
- 极窄窗口下 `100vw - 96px` 列宽过小会退化为单列自然流，属可接受边界。

### 发布与提交

- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（exe 更新于 2026-08-07 00:48），Release 启动存活并打开真实 EPUB 验证阅读控件齐全。
- Debug/Release x64 构建 0 警告 0 错误；22 项测试两个配置全部通过。
- 本轮提交：`1444cb9 fix: fit reader content to viewport`，仅包含本次源码与 `AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 22. 连续滚动接章与分页点击修复（2026-08-07）

### 根因（已用真实运行路径证实，非推测）

两个交互失效是同一个根因：`WebView2.Settings.IsScriptEnabled = false`（EPUB 安全策略）会**冻结页面的 JS 事件派发**。

用独立 WebView2 复现程序（WinForms + Microsoft.Web.WebView2，与生产一致的 `IsScriptEnabled=false` + 生产注入脚本）实测：

- `IsScriptEnabled=false` 时：直接 `postMessage` 探针可达宿主（`{"type":"probe","ok":true}` 被 `WebMessageReceived` 收到），注入脚本确实安装（`__kkindleNavBound="bound"`）、DOM 读写在 `ExecuteScriptAsync` 下全部可用（`scrollTop/scrollHeight/scrollLeft` 等），但 `document`/`window`/`html`/`body` 上的 **scroll、pointerdown、click、keydown 监听全部 0 次触发**——即使真实鼠标滚轮已让 `scrollTop` 移动（`0→3000`）、真实鼠标点击已送达页面（对照组 `IsScriptEnabled=true` 时 `docScroll` 362 次、`reader-click next/prev` 消息都正常到达）。
- 结论：旧注入脚本（`InstallReaderNavigationHooksAsync`）依赖页面事件 + `window.chrome.webview.postMessage`，在该安全设置下**整条链路是死的**。滚动模式的章节接续和分页模式的点击翻页因此都不触发，与用户反馈完全一致。键盘左右键不受影响是因为 XAML `RootGrid_KeyDown` 有宿主侧兜底。

### 本次改动（最小必要，未动标题栏/无关 UI）

1. `src/Kkindle.App/MainWindow.ReaderFeatures.cs`：
   - 删除已证明失效的页面注入钩子（`InstallReaderNavigationHooksAsync`）、`WebMessageReceived` 消息处理与 `HandleReaderScrollMessage`。
   - 新增宿主侧滚动轮询 `PollReaderScrollAsync`：滚动模式每 150 ms 经 `ExecuteScriptAsync` 读取真实滚动容器（`document.scrollingElement`）的 `scrollTop/scrollHeight/clientHeight`，用边沿触发 + 连续锁推进/回退章节：滚到底部（`st+ch >= sh-48`）进下一章，滚到顶部（`st<=48`）回上一章；边沿状态在每个 `NavigationCompleted` 后 `PrimeReaderScrollEdgesAsync` 重新对齐（避免新章加载在顶部时立即弹回上一章、或回退到章末时立即弹回下一章）；新增对称的“强制推进”分支解决短章节/滚动条直拖等“未经过中段”的卡死。
   - 新增低级鼠标钩子（`WH_MOUSE_LL`）驱动分页点击分区：只观察 WebView 宿主屏幕矩形内的左键点击，按下/抬起位移 ≤12px 视为点击（拖拽选字不触发），换算 viewport 相对坐标后 `elementFromPoint` 检查链接/输入控件与文本选择以保持旧行为，然后左 1/3 上一页、右 2/3 下一页，复用生产 `TurnReaderPageAsync`（章节内按实际 `scrollWidth/clientWidth` 推进 `scrollLeft`，章界跨章）。工具栏/目录/助手/底栏都在宿主矩形之外，不会被误触发。
   - 修正 `ExecuteScriptAsync` 返回值解析：脚本改为返回裸对象（而非 `JSON.stringify` 字符串），避免 `JsonDocument` 解析到字符串后 `TryGetProperty` 抛异常被吞掉导致轮询静默失效。
2. `src/Kkindle.App/MainWindow.xaml.cs`：
   - 移除 `WebMessageReceived` 注册与调用；`NavigationCompleted` 用 `PrimeReaderScrollEdgesAsync()` 替换旧的页面钩子安装；`ReaderFlowButton_Click` 切换模式后重新对齐边沿状态。
   - 打开 EPUB 时 `StartReaderScrollPoll()` + `InstallReaderMouseHook()`，关闭阅读器/窗口关闭时 `StopReaderScrollPoll()` + `UninstallReaderMouseHook()`。
3. 保留：`IsScriptEnabled=false`、EPUB 路径白名单、键盘左右键、禅模式、翻页动画、目录跳转、视口自适应（`ScheduleReaderRelayout`/`ClampReaderScrollAsync`）、短章节 `SkipShortChapterIfNeededAsync`。`MainWindow.xaml` 无改动。

### 真实定向验证（真实 EPUB《策略思维》，Release 发布版 + Debug 实测）

- 滚动模式（真实滚轮 + 程序化滚动到真实底部）：
  - Debug 构建真实滚轮把第 1 章（封面）滚到底部后，`poll-next` 推进到第 2 章，新章 `prime st=0`（从顶部开始）。
  - 程序化滚动（任务允许的等效路径，仅移动真实滚动容器位置，章节判定全走生产轮询）：第 1→2→3→4 章依次滚到底部均自动进入下一章且每章 `prime st=0`（新章从顶部）；最后第 4 章滚到底不再前进（正确停在末章）；随后第 4→3→2→1 章滚到顶部均自动回到上一章且 `prime` 落在章末（回退保持原阅读位置）。
  - Release 发布版验证：真实滚轮在自动化环境中因 WebView2 合成岛输入路由限制（见下）只对首屏可靠，滚动接章在 Debug 下已用真实滚轮实测一次、程序化路径全章验证；发布版与 Debug 为同一代码。
- 分页模式（真实鼠标点击，Release 发布版 UIA 可观测章节计数）：
  - 右 2/3 连续点击：`已读 1/4 → 2/4 章`，章节内 `scrollLeft` 每击推进约一个视口宽（842 px，实测 `sl 0→842.4→1684.8`），章界正确跨章；左 1/3 点击逐页回退并回到上一章。
  - 与旧行为一致的守卫保留：拖拽（位移>12px）、点击链接/输入控件、产生文本选择时不翻页。
- 自动化环境限制（需真实桌面人工复核）：本会话无法把真实鼠标滚轮可靠投递到导航后的 WebView2 合成渲染内容（WebView HWND 为 0x0 composition island），因此“连续滚动跨章后继续滚轮”在自动化中未能端到端复跑；已用程序化滚动验证全部章节的接续/回退逻辑（生产轮询读取的是真实 DOM 滚动位置，与输入来源无关）。建议人工在真机上连续滚动读完一章后再滚一下确认。

### 构建、发布与提交

- Debug/Release x64 完整解决方案构建均为 0 警告、0 错误；22 项测试两个配置全部通过。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新），Release 启动存活、真实 EPUB 分页点击验收通过（右 2/3 跨章、左 1/3 回退），阅读控件齐全。
- 本轮提交：`c91972b fix: repair reader page interaction`，仅包含 `MainWindow.xaml.cs`、`MainWindow.ReaderFeatures.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 23. 阅读器生产力工具大升级（2026-08-07）

参考 Readest 的阅读排版、进度、书签、搜索、选区动作栏、标注导出、阅读统计和 CJK 思路（只借鉴功能与 UX，不复制其 AGPL 代码），在 Kreader 阅读器上完成本轮升级。

### 功能与数据表

1. **阅读排版设置**（每本书独立）
   - “更多”菜单新增“阅读排版设置”弹层：字号（80%–180%，与工具栏 A−/A+ 同步）、行高（1.3–2.6）、正文最大宽度（480–1200 px）、左右边距（24–160 px）、正文字体/CJK 覆盖（系统默认/微软雅黑/宋体/黑体/楷体/等线/思源宋体-Noto Serif CJK）、竖排开关、恢复默认值。
   - 持久化到 SQLite `ReaderLayoutSettings`（按 `BookFileId` 一行），下次打开自动恢复；设置变化经 160ms 防抖重新调用 `ApplyReaderAppearanceAsync()` + `ClampReaderScrollAsync()`，不破坏视口自适应与分页列宽重算。
   - 未恢复颜色切换按钮（按用户要求移除）；滚动/分页与翻页动画沿用工具栏与“更多”菜单既有功能。
2. **阅读进度与断点恢复**
   - SQLite `ReaderProgress`（按 `BookFileId` 一行）：章节路径、fragment、章节索引、滚动位置、进度百分比、流动模式、更新时间；`CREATE TABLE IF NOT EXISTS` 安全初始化。
   - 保存时机：章节导航完成、翻页、连续滚动轮询（≥4s 节流）、流动模式切换、关闭阅读器/窗口关闭（强制保存）。
   - 重新打开自动恢复章节与位置；`FlowMode` 不同（如上次分页、本次滚动）时不复用上次像素位置，新章从顶部开始；滚动用 `scrollTop`、分页/竖排用 `scrollLeft`。
3. **书签**
   - 工具栏“书签”按钮 + `Ctrl+B`：在当前章节/fragment/文本锚点处添加或取消书签（带章节标题与简短引文）。
   - 目录面板新增“目录 / 书签”标签页；书签列表显示标题、引文、创建时间，点击跳转到对应章节（按引文文本滚动画中），行内 × 删除。持久化到 `ReaderBookmarks`。
4. **书内搜索**
   - 工具栏“搜索”按钮 + `Ctrl+F` 打开 Popup 搜索面板（浮于 WebView2 之上，不遮挡三栏布局，窄窗口自适应）。
   - 复用 `BookContentChunks` 本地 FTS（trigram），异常时 LIKE 回退；结果展示章节、匹配片段与数量；点击结果复用 `_pendingReaderChunkOffset` 跳到对应章节片段；全程不调用 AI、不上传正文。
   - 搜索面板打开时禁用分页点击翻页（`HandleReaderZoneClickAsync` 增加 `_readerSearchVisible` 守卫）。
5. **选中文字快捷工具栏**
   - 黑白动作条：复制 / 划线 / 批注 / AI 解释 / 书内搜索。
   - 因 `IsScriptEnabled=false` 冻结页面事件，不使用 JS click 监听；改为宿主侧 300ms 轮询 `window.getSelection()` 读取选区文本与 `getBoundingClientRect()`，按 WebView 宿主屏幕矩形换算 DIP 坐标后定位 Popup。
   - 动作全部复用生产代码路径（`SaveReaderAnnotationAsync`、`ReaderAiExplainSelectionButton_Click`、`RunReaderSearchAsync`）；划线/批注沿用既有锚点、重叠规则与重新加载恢复。
6. **划线/批注导出**
   - “划线与笔记”页新增“导出 Markdown”和“导出文本”，内容含书名、作者、章节标题、原文、批注、创建时间与可回到原文的定位（chapterPath#fragment + 偏移）。
   - 使用 `FileSavePicker`；取消有明确状态（“已取消导出，笔记未被修改”），写入失败显示原因；纯本地只读格式化，不上传。导出逻辑独立为 `src/Kkindle.Infrastructure/ReaderAnnotationExport.cs` 以便测试。
7. **阅读统计**
   - 目录面板新增统计行：已读 x/y 章、全书百分比、累计阅读时长、本次阅读时长；百分比用“章节索引 + 章节内比率”计算，与实际位置一致。
   - 时间统计：仅窗口激活且阅读面板可见时每秒累计，每 30 秒落库，关闭阅读器/窗口强制落库；`ReaderReadingStats` 持久化累计秒数、进度快照、已读/总章节。
8. **CJK 阅读增强**
   - CJK 字体覆盖与 fallback 栈应用到正文；`line-break: strict`、`word-break: normal`、两端对齐、`overflow-wrap: anywhere` 保证中文长句不被强制挤成横向溢出；`ruby { ruby-align: center }` 与 `rt { font-size: 0.5em }` 支持 furigana/ruby 显示。
   - 滚动模式下支持 `writing-mode: vertical-rl` 竖排（列沿水平方向流动，滚动/翻页/进度/接章逻辑均按横向轴处理）；分页模式竖排不生效并显示提示，未做假功能。
   - 不破坏中文 EPUB 元数据、正文索引、脚注与划线/批注定位。

### 工程约束保持

- `WebView2.Settings.IsScriptEnabled=false` 不变；EPUB 自带脚本不运行，导航仍限制在当前 PDF/EPUB 缓存目录。
- 未改 Kindle `system` 访问边界，未读取/修改 calibre 数据库。
- SQLite 变更全部使用 `CREATE TABLE IF NOT EXISTS`，不影响既有表与数据；`ReaderDataService.InitializeAsync()` 保持幂等。
- 三栏布局、禅模式、跨章节滚动、分页鼠标分区（左 1/3 上一页/右 2/3 下一页）、左右键、翻页动画、目录跳转、AI 问答与划线/批注既有功能均保留。

### 数据库变更

```text
ReaderProgress        (BookFileId PRIMARY KEY, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition, ProgressPercent, FlowMode, UpdatedAt)
ReaderBookmarks       (Id PRIMARY KEY, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, Title, Quote, CreatedAt) + IX_ReaderBookmarks_BookFile
ReaderLayoutSettings  (BookFileId PRIMARY KEY, BookId, FontScale, LineHeight, MaxWidth, BodyPadding, FontFamily, FlowMode, VerticalWriting, UpdatedAt)
ReaderReadingStats    (BookFileId PRIMARY KEY, BookId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt)
```

### 启动崩溃修复（本轮重要发现）

本轮曾出现 `0xC000027B` + `Microsoft.UI.Xaml.dll` + `combase 0x802B000A` 的启动崩溃，日志定位为 `InitializeComponent()` 抛 `XamlParseException: Failed to assign to property 'RangeBase.Minimum/Value'`。根因：Slider/ComboBox 的 `ValueChanged`/`SelectionChanged` 在 XAML 解析给 `Minimum`/`Maximum`/`Value` 赋值时提前触发，处理器访问尚未创建的兄弟控件导致空引用。修复：`ReaderLayoutSettingChanged` 与 `ReaderFontFamilyBox_SelectionChanged` 增加 `AreReaderLayoutControlsReady()` 空值守卫。此坑已写入第 12 节基线备忘。

### 验证结果

- Debug/Release x64 完整解决方案构建：0 警告、0 错误；28 项测试（新增 `ReaderProductivityTests`：进度保存/恢复、书签增删列、排版设置持久化、阅读统计累计、FTS+LIKE 搜索、Markdown/纯文本导出）两个配置全部通过。
- 真实 EPUB 定向验证（先《策略思维》4 章 146 目录项，后 CJK 测试 EPUB 3 章含 ruby/脚注）：
  - 准备/索引：章节数与片段数正确；FTS 搜索“断行规则”与 LIKE 回退“标点”均命中正确章节。
  - 进度保存/恢复、书签新增/删除、排版设置持久化、阅读统计累计、批注导出文件存在且内容含书名/章节/原文/批注/时间/定位、批注定位可回到原文章节文件，全部通过（验证程序退出码 0）。
  - 生产库验证：发布版启动后 `data/kkindle.db` 已出现 `ReaderProgress`、`ReaderBookmarks`、`ReaderLayoutSettings`、`ReaderReadingStats` 四个新表。
- Release 发布版启动存活验证通过（两次运行均 5 秒以上存活），标准 `publish` 目录已更新。
- 自动化边界：与第 21/22 节一致，本会话无法把真实鼠标/键盘事件可靠投递到 WebView2 合成岛，因此“选区工具栏点击（复制/划线）”“书签列表点击跳转”“导出保存对话框”“竖排渲染效果”等端到端输入路径未在自动化中触发；已用生产代码可观测状态（同一代码路径、数据层结果、启动存活、DB 表结构）与真实 EPUB 数据层定向验证覆盖。竖排渲染与窗口内视觉验收建议人工在交互桌面复核一次。

### 发布与提交

- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新）。
- 本轮提交：`feat: expand reader productivity tools`，仅包含本轮相关源码与测试：`MainWindow.xaml`、`MainWindow.xaml.cs`、`MainWindow.ReaderFeatures.cs`、新增 `MainWindow.ReaderTools.cs`、`Core/ReaderModels.cs`、`Infrastructure/ReaderDataService.cs`、新增 `Infrastructure/ReaderAnnotationExport.cs`、新增 `tests/ReaderProductivityTests.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。
- 未完成风险（CJK/交互边界）：分页模式竖排暂不生效（有提示）；竖排与超长单元素布局未做专项视觉回归；选区工具栏、书签跳转、导出对话框与竖排渲染的端到端输入自动化受限，需人工复核。

## 24. 分页正文排版修复：默认横排、列宽对齐与列边界吸附（2026-08-07）

### 根因（用真实 EPUB + 生产注入 CSS 在无头 Chromium 复现，非推测）

用户反馈分页模式下中文正文被拆成多个很窄的横向文字列/片段、列间大空白、左边第一段被裁掉、阅读顺序错乱。复现《策略思维》EPUB 第 1 章 + 生产注入 CSS（当前库中 80% 字号、`FlowMode=1`、`MaxWidth=800`）：

```css
/* 修复前 */
html { height: 100%; overflow: hidden !important; }
body { height: 100%; overflow: visible !important; padding: 48px 24px 64px !important;
       box-sizing: border-box; column-width: calc(100vw - 96px); column-gap: 48px;
       column-fill: auto; max-width: none !important; }
```

实测（视口 `innerWidth=1318`，窗口 1344）：

- 生产当前 CSS：`column-width` 计算值 1222px + 间距 48px = **1270px ≠ 1318px 视口宽**。整章水平分成 12 列（`scrollWidth=15792`），但翻页脚本按 `window.innerWidth`（1318px）推进 `scrollLeft`，与列边界（每 1270px 一列）错位。每翻一页漂移 48px，累计几页后屏幕中央出现两个半列 + 大空白；断点恢复（库中 `ScrollPosition=842`）落在一列中间，正好复现“左边第一段被裁掉”。
- 根因一句话：**列宽 + 列间距 ≠ 视口宽**。分页列必须在正文左右内边距之内排布，`column-width` 应取 `calc(100vw - 48px)`（与左右 24px 内边距之和正好等于视口宽），而不是 `calc(100vw - 96px)`。
- 附带风险：分页 CSS 未显式重置 `writing-mode`，若 EPUB 自带竖排规则会污染默认横排；持久化的 `ReaderLayoutSettings` 若有 NaN/越界值会把字号/宽度推到不可读范围。

### 本次改动

1. `src/Kkindle.App/MainWindow.xaml.cs` — `ApplyReaderAppearanceAsync()` 分页 CSS 修正：
   ```css
   html { height: 100%; overflow: hidden !important; writing-mode: horizontal-tb !important; }
   body { height: 100%; overflow: visible !important; padding: 48px 24px 64px !important; box-sizing: border-box;
          writing-mode: horizontal-tb !important; column-width: calc(100vw - 48px); column-gap: 48px;
          column-fill: auto; column-count: auto !important; max-width: none !important; }
   ```
   `column-width + column-gap = 100vw`，每列恰好一视口宽；默认横排（`writing-mode: horizontal-tb !important`），竖排只在滚动模式显式开启。
2. `MainWindow.xaml.cs` — 新增 `SnapReaderPaginationAsync()`：把 `scrollLeft` 吸附到 `paddingLeft + N × clientWidth` 列边界并钳制到最大滚动范围、固定 `top: 0`。分页模式下每次应用外观后自动吸附；`ClampReaderScrollAsync()` 分页分支改为走吸附；`MoveReaderToEndAsync()` 分页回退到章末后吸附。
3. `MainWindow.ReaderTools.cs` / `ReaderFeatures.cs` / `ReaderAi.cs` — 断点恢复 `ApplyReaderRestorePositionAsync()`、书签 `ScrollToPendingReaderBookmarkAsync()`、批注 `ScrollToPendingReaderAnnotationAsync()`、搜索片段 `ScrollToPendingReaderChunkAsync()` 在分页模式下均追加列边界吸附，避免任意 `scrollIntoView` 停在半列。
4. 滚动模式 CSS 追加显式清场：`body { column-width: auto !important; column-count: auto !important; column-gap: normal !important; writing-mode: horizontal-tb !important; }`，清除分页残留列与竖排污染，回到单列自然纵向流。
5. `src/Kkindle.Core/ReaderModels.cs` — 新增 `ReaderLayoutDefaults.Normalize()`：对持久化排版设置做有限值收敛（字号 0.8–1.8、行高 1.3–2.6、正文宽度 480–1200、边距 24–160、NaN/非有限值回退默认、FlowMode 非法归 0）。`LoadReaderSessionDataAsync()` 加载设置时统一归一化；不清空任何用户阅读数据，只修正非法排版字段。
6. 未改：`IsScriptEnabled=false`、导航白名单、三栏布局、禅模式、翻页动画、书签/搜索/选区/导出/统计/AI 等全部既有功能；`MainWindow.xaml` 无改动。

### 真实定向验证（《策略思维》EPUB 第 1 章，无头 Chromium + 生产注入 CSS/吸附脚本，视口 1344×1000）

- 分页首页：`innerWidth=1318`、`colW=1270px`、`colGap=48px`、`colW+colGap=1318=视口宽`；`scrollWidth=15792`（整章按一视口一列分页）；可见段落矩形 `left≥0`、`right≤1270`，无左侧裁切、无窄列。
- 列边界吸附：初始 `scrollLeft=0 → 吸附 24`（首列）；断点 842 → 吸附 1342（第 2 列整页），不再出现半列。
- 翻页推进：`scrollLeft` 每击推进 1318px（`1342→2660→3978→5296→6614`），全部落在 `24 + N×1318` 列边界；`scrollTop` 恒为 0；每页可见段落矩形 `left≥0`、`right≤1270`（跨列段落的 union box 允许超出，但其可见片段完整）。
- 换视口重排：窗口 900×800（`innerW=874`、`colW=826`、列边界 `24+N×874`）与 1600×1000（`innerW=1574`、`colW=1526`、列边界 `24+N×1574`）均保持“一视口一列 + 翻页对齐”，证明缩放/收目录/收助手/禅模式后按新 viewport 重排成立。
- 滚动模式：`bodyMaxW=800px` 居中单列、`colW=auto/colGap=normal`、`writing-mode=horizontal-tb`、`scrollHeight=13697` 自然纵向滚动；分页列与竖排残留被清除。
- Release 发布版启动存活 5 秒以上（smoke test 通过）。

### 自动化边界

- 与第 21/22 节一致，本会话无法向 WebView2 合成岛可靠投递真实鼠标/滚轮，因此分页点击/键盘翻页未在自动化中触发；已用生产代码同路径的无头 Chromium 验证列对齐数学与吸附脚本（`scrollLeft` 推进量、可见段落矩形、计算样式全部为可观测真实数据）。建议人工在交互桌面翻几页 + 收放目录/助手各确认一次。

### 构建、测试与发布

- Debug/Release x64 完整构建 0 警告 0 错误；33 项测试全部通过（新增 `tests/Kkindle.Tests/ReaderLayoutDefaultsTests.cs`：默认值横排可读、越界收敛、NaN 回退、FlowMode 非法归 0、合法设置原样保留）。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新）。
- 本轮提交：`fix: restore reader pagination layout`，仅包含 `MainWindow.xaml.cs`、`MainWindow.ReaderTools.cs`、`MainWindow.ReaderFeatures.cs`、`MainWindow.ReaderAi.cs`、`Core/ReaderModels.cs`、`tests/Kkindle.Tests/ReaderLayoutDefaultsTests.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 25. EPUB 图片/封面视口自适应（2026-08-07）

### 根因（用截图对应真实 EPUB + 生产注入 CSS 在无头 Chromium 复现，非推测）

用户反馈：分页模式打开《規模/Scale》EPUB 封面章节，封面从正文区域内开始但被按很大宽度铺开，底部和标题明显超出正文可视高度，在 WebView 底部被裁切；滚动模式担心普通插图横向溢出或全部变成小缩略图。

真实 EPUB：`data/library/6efd4f1ba0ed4a2abdc2f0390edc7299/…(z-library.sk, 1lib.sk, z-lib.sk).epub`（《規模》[英]傑佛瑞·韋斯特，zh-CN）。封面章节 `Text/cover.xhtml` 是 `<svg width="100%" height="100%" viewBox="0 0 1536 2048" preserveAspectRatio="xMidYMid meet"><image width="1536" height="2048" xlink:href="../Images/cover.jpg"/></svg>`，封面位图 `Images/cover.jpg` 1536×2048（3:4 竖版）。

修复前分页 CSS 只有 `img, svg { display: block; max-width: 100% !important; height: auto !important; margin: 1.8em auto !important; }`。在截图对应视口 1053×796 实测（无头 Chromium + 生产 CSS）：

- 修复前：`svg` 计算宽 1005px（= 整列宽）、高 1340px，`getBoundingClientRect` 底部到 1388，而正文内容盒底部仅 732 → 底部被裁切 656px，标题被推到可视区外，正是截图现象。
- 根因一句话：分页图片只约束了 `max-width`，没有按当前页内容盒高度约束 `max-height`，且 `svg` 未强制 `width:auto`，高竖版封面被按整列宽铺开。

### 本次改动

1. `src/Kkindle.App/MainWindow.xaml.cs` — `ApplyReaderAppearanceAsync()` 图片 CSS 重做（分页/滚动两套）：
   - 分页：`img { width:auto !important; height:auto !important; max-width:100% !important; max-height:calc(var(--kkindle-page-content-h, 100vh) - 3.6em) !important; object-fit:contain; margin:1.8em auto !important; }`，`svg` 同样 `width:auto/height:auto`（封面 svg 贴合 3:4 比例而非铺满列宽）；`--kkindle-page-content-h` 由注入脚本用 `body.clientHeight - paddingTop - paddingBottom` 实测正文内容盒高度并写到 `documentElement`，图片 `max-height` 基于真实 WebView viewport/内容盒，不是整个窗口的 `100vw/100vh`；`3.6em` 即图片自身 1.8em 上下外边距，图片+边距整体不超当前页，不裁切、不变形（width/height 双 auto + max 双约束即 contain 语义，可覆盖 EPUB 内联/样式 width/height）。
   - `.kkindle-cover`：首个大图（按 `naturalWidth/naturalHeight`，未解码时回退 `width/height` 属性，与书名/文件名无关）加类后 `max-height` 收紧到 `calc(var(--kkindle-page-content-h) - 6em)`、外边距 1em，封面与其后标题可同页显示；SVG `<image>` 命中时把类加回父 `svg`。
   - 滚动：只保留 `height:auto !important; max-width:100% !important`（不强制 `width`，保留 EPUB 自身百分比宽度如 `div.chatu-part img{width:40%}` 装饰图），图片按自然比例显示、可纵向滚动、无横向溢出；`svg`/`svg image` 同步约束。
   - 新增 `FitReaderImagesAsync()`（分页封面识别，在 `ApplyReaderAppearanceAsync` 内调用）与 `RetryReaderImageFitAsync()`（`NavigationCompleted` 后 250ms/950ms 主机侧重试，图片延迟解码后补加封面类并重新吸附）。保留 `IsScriptEnabled=false`、导航白名单、列宽/翻页/吸附/跨章逻辑不变。
2. 未改：`WebView2.Settings.IsScriptEnabled=false`（`ConfigureReaderWebView()`）、EPUB 路径白名单、三栏布局、禅模式、书签/搜索/选区/导出/统计/AI、既有未提交的 `App.xaml` 与 `.opencode/`。

### 真实定向验证（真实《規模/Scale》EPUB，无头 Chromium + 生产 CSS/识别脚本）

- 封面章节分页（视口 1053×796，与截图 WebView 一致）：
  - 封面 `image` 渲染 `getBoundingClientRect` = 426.6×568.8，宽高比 0.75 与自然 1536/2048 完全一致（aspectPreserved=true）；矩形 top 67.2 / bottom 636，落在正文内容盒 [48, 732] 内（withinHoriz/withinVert/noBottomClip 全 true）；coverClass=true。
  - 修复前对照：svg 1005×1340、底部 1388，超内容盒底部 732 达 656px（原截图裁切）。
- 换视口重排（900×700，模拟收起目录/助手/禅模式后的窄正文）：封面自动重排为 354.6×472.8，比例 0.75 不变、仍在内容盒 [48, 636] 内、无裁切。
- 普通插图章节 `Text/03.xhtml`（19 张真实图：1196×887、1134×1200、640×1200、768×304、536×500、68×50 小图标等）：
  - 滚动模式：19/19 `withinHoriz/withinVert/noBottomClip/aspectPreserved` 全 true；`scrollWidth=clientWidth=1038`（无横向溢出）、`verticalScrollable=true`；640×1200 竖图按自然尺寸 640×1200 显示（未被压缩成缩略图），68×50 小图标原样。
  - 分页模式：19/19 `withinVert/noBottomClip/aspectPreserved` 全 true；普通图 `max-height` 计算值 614.88px（684−3.6em），封面类 568.8px；列间横向流动为分页设计行为，由 `SnapReaderPaginationAsync` 吸附。
- 插图章节 `Text/chapter01.xhtml`（310×310 图 + h1 标题）：分页渲染 310+10px 边框×2=330×330、比例 1 保持、标题首屏可见；滚动模式保留书自带 `div.chatu-part img{width:40%}`（265.6+20=285.6）设计意图，无横向溢出。

### 自动化边界

- 与第 21/22/24 节一致：本会话无法把真实鼠标/滚轮投递到 WebView2 合成岛，分页点击翻页未在自动化中触发；本次验证用真实 EPUB DOM + 生产注入 CSS/识别脚本在无头 Chromium 中实测 `naturalWidth/naturalHeight/clientWidth/clientHeight/getBoundingClientRect` 与内容盒、视口换算，属真实渲染数值。建议人工在交互桌面打开《規模/Scale》封面与含图章节，分别用分页/滚动模式翻页并缩放窗口各确认一次。
- 未验证边界：SVG 封面在极窄视口（<760px）下仍按比例收缩，属可接受边界；`object-fit:contain` 在 img 双 auto 约束下无副作用；EPUB 若用 `<picture>`/`srcset` 多源图，`naturalWidth` 取当前命中源，识别/约束同样成立（未专项验证）。

### 构建、测试与发布

- Debug/Release x64 完整解决方案构建：0 警告、0 错误；33 项测试（Debug/Release）全部通过。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新），Release 启动存活验证通过。
- 本轮提交：`fix: fit epub images to reader viewport`，仅包含 `src/Kkindle.App/MainWindow.xaml.cs` 与 `AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 26. 阅读器关闭卡死修复与章节平滑过渡（2026-08-07）

### 根因（X 退出按钮卡死，已用真实运行路径证实）

用户反馈：点击 Kreader 顶部自绘 X 退出按钮后窗口卡死/无响应。定位到两个可导致 UI 线程阻塞的根因：

1. **低级鼠标钩子（`WH_MOUSE_LL`）回调跨线程访问 XAML，与 `UnhookWindowsHookEx` 双向死锁**。钩子回调运行在系统钩子线程，原实现为每个系统级鼠标事件读取 `ReaderPane.Visibility`、`_readerFlowMode` 等 DependencyObject，并在左键抬起分支同步执行 `GetReaderWebViewScreenRect()`（`ReaderWebViewHost.TransformToVisual` 等 XAML 调用）。这些跨线程 XAML 访问可能阻塞等待 UI 线程。用户点击 X 时：UI 线程进入 `CloseReader()` → `UninstallReaderMouseHook()` → `UnhookWindowsHookEx`（**等待在途钩子回调返回**），而钩子回调正阻塞在 XAML 访问上等待 UI 线程 → 双向死锁，整窗无响应。
2. **窗口关闭在 UI 线程同步等待永不返回的 WebView 脚本**。`MainWindow_Closed` 原实现 `FlushReaderSessionAsync().GetAwaiter().GetResult()` 同步阻塞 UI 线程；若 WebView 正处于导航/关闭中，`ExecuteScriptAsync` 可能永不返回，窗口直接冻结。另外原 `CloseReader()` 在重置 `_readerChapterIndex`/`_readerChapters` 之后才经 `EndReaderSession()` 触发刷盘，关闭时的进度捕获实际读到空状态（进度未保存）。

### 本次改动（关闭流程，按用户要求顺序）

1. `MainWindow.ReaderFeatures.cs` — **钩子回调彻底不再触碰 XAML**：新增 `_readerHookEnabled`（volatile）与缓存屏幕矩形 `_readerWebViewScreenRect`；回调只读这两个普通字段判定点击，左键抬起点击经 `DispatcherQueue.TryEnqueue` 投递到 UI 线程后由 `HandleReaderZoneClickAsync` 处理（分区点击逻辑不变）。`GetReaderWebViewScreenRect()` 在 UI 线程计算并刷新缓存，`ApplyReaderPanelLayout()`/`ReaderContentPanel_SizeChanged` 也刷新缓存。`UninstallReaderMouseHook()` 先清 `_readerHookEnabled` 再 `UnhookWindowsHookEx`，回调因只读普通字段总是快速返回，卸载不会锁死 UI 线程。
2. `MainWindow.xaml.cs` — **`MainWindow_Closed` 移除 `.GetAwaiter().GetResult()` 同步等待**：先停止全部阅读器机制（钩子、滚动轮询、工具计时器、章节过渡/重排取消令牌），再 `_ = FlushReaderSessionSafelyAsync(skipWebViewCapture: true)` 异步落库（有界、非阻塞、失败不阻止关闭）。
3. `MainWindow.xaml.cs` — **`CloseReader()` 重写为幂等非阻塞关闭**（`_readerCloseInProgress`/`_readerCloseRequested` 守卫）：顺序为——① 先停钩子/滚动轮询/工具计时器，取消章节过渡动画与重排防抖，复位宿主变换；② 关闭全部 Popup（助手/设置/搜索/选区/禅模式）；③ 隐藏 `ReaderPane`/`ReaderBrandText` 返回书架；④ 以 `skipWebViewCapture=true` 发起有界异步保存（最后捕获的 `_readerLastProgress` + 阅读秒数，全程不碰 WebView），再 `EndReaderSession()` 清理；⑤ 重置会话状态并 `Navigate("about:blank")`。重复点击 X/返回书架/窗口关闭均幂等。
4. `MainWindow.ReaderTools.cs` — **`FlushReaderSessionAsync(skipWebViewCapture)` 重构**：关闭路径不调用 `ExecuteScriptAsync`（用 `_readerLastProgress`），并使用 `CancellationToken.None` 避免被 `EndReaderSession` 的取消令牌打断；新增 `FlushReaderSessionSafelyAsync()`，`WaitAsync(1500ms)` 有界等待，任何超时/异常被吞掉，保存失败绝不阻止关闭。阅读秒数在首个 await 前同步清零，避免统计计时器与关闭刷盘并发重复计数。
5. `MainWindow.ReaderFeatures.cs` — `EndReaderSession()` 不再自行刷盘（刷盘由 `CloseReader()`/`MainWindow_Closed()` 在会话字段置空前发起），只负责取消/清理。
6. `ReaderWebView_NavigationCompleted` 全程 try/catch，`_readerCloseRequested` 时直接返回；未完成的章节过渡在关闭时被强制取消并复位变换。

### 本次改动（章节平滑过渡）

所有真实章节切换路径统一收敛到 `ShowReaderChapterAsync` / `NavigateReaderSourceAsync`，在已验证的宿主变换（`ReaderWebViewHost` 的 `Opacity/Scale/TranslateX`，即既有翻页动画同一机制，不遮挡 composition island）上播放 130ms（出）+ 190ms（入）过渡，不阻塞交互：

- 覆盖路径：滚动自动接章（`PollReaderScrollAsync` 底部/顶部边沿推进与回退）、上一/下一章按钮与分页跨章（`TurnReaderPageAsync` 章界）、目录跳转（`ReaderTocList_SelectionChanged`）、进度条跳转（`ReaderProgressSlider_ValueChanged`）、书签/批注/AI 来源/书内搜索片段跳转。
- 动画模式复用现有“无动画/仿真/左右滑动”菜单：仿真 = 淡出淡入 + 轻微缩放；左右滑动 = 按方向水平平移；无动画 = 立即切换。**默认模式从“无动画”改为“仿真”**，保证切章默认平滑；时长落在 140–260ms 目标区间。
- `AnimateReaderPageTurnAsync` 增加 `CancellationToken`：新过渡/关闭会取消旧动画并复位变换，杜绝旧动画残留导致内容偏移。`NavigationCompleted` 仅在未关闭时播放入场动画，并在 `PrimeReaderScrollEdgesAsync` 之后释放过渡守卫。
- 过渡期间用 `_readerTransitionActive` 守卫滚动轮询与分页分区点击，防止动画/导航中误触接章；`NavigationCompleted` 释放，另有 3 秒看门狗兜底避免永不释放。
- 关闭与动画互不等待：`CloseReader()` 第一步取消章节过渡令牌并清除 `_readerPendingTurnInAnimation`，动画任务以 `OperationCanceledException` 退出并复位变换。
- 未改 `WebView2.Settings.IsScriptEnabled=false`（`ConfigureReaderWebView()`）与 EPUB 导航白名单；`MainWindow.xaml` 仅调整动画菜单默认勾选（仿真）。

### 定向验证（真实 EPUB《規模》31 章，Release 发布版 + Debug 实测）

1. **X 关闭**：Release 发布版真实点击顶部 X（UIA Invoke）→ “关闭阅读器”按钮从 UIA 树消失耗时 **176–196ms**，进程存活、窗口响应（标题可读）、返回书架（“开始阅读”再次可见）。连续双击 X 不卡死且幂等（约 738–748ms 内完成）。目录面板“返回书架”按钮关闭耗时 **176–191ms**。Debug 构建重复验证一致（X 184ms / 返回书架 176ms）。所有关闭路径全程无窗口冻结、无进程退出。
2. **章节过渡**：以 TOC 搜索过滤后点击不同章节条目（真实跨 spine 章节）——默认“仿真”模式 5/31→1/31；切“无动画”2/31→22/31；切“左右滑动”22/31→23/31；每次约 2.1–2.3s（含导航与后处理），章节计数更新正确，进程全程存活。三种模式均无卡死。
3. 打开/翻页/切章/关闭/重开完整闭环多次复跑，进度断点恢复正常（重开后回到上次章节 23/31），未出现阅读器残留或 WebView 锁死。

### 自动化边界（未验证项，需人工复核）

- 本环境无法读取像素，动画的“视觉效果”未做像素验收；已确认的是：过渡期间进程无冻结、导航完成、章节计数正确更新、宿主变换在 `NavigationCompleted` 复位（与既有翻页动画同一代码路径）。
- WebView2 合成岛输入限制（与前几轮一致）：无法把真实滚轮可靠投递到导航后的正文，**滚动自动接章**端到端未在自动化中触发；其代码路径与已验证的目录/按钮跨章完全一致（`PollReaderScrollAsync` → `ShowReaderChapterAsync`）。
- **分页分区点击**（左 1/3 上一页/右 2/3 下一页）跨章端到端同样受合成岛输入限制未在自动化中触发；钩子回调只读缓存 + 点击投递 UI 线程的改动已在 X 关闭验证中覆盖（钩子安装/卸载全程无锁死）。建议人工在真机上滚完一章、分页点到章界各确认一次过渡效果。
- 窗口关闭（非 X）时“最后进度落库”在自动化中未专门验证进程退出前是否写完；周期保存（每 4s 节流 + 每 30s 统计 + 切章保存）与关闭刷盘（有界 1500ms）共同保障，书签/批注为即时保存不依赖关闭。

### 构建、测试与发布

- Debug/Release x64 完整解决方案构建：0 警告、0 错误；33 项测试（Debug/Release）全部通过。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新，12:15），Release 启动存活 + 真实 EPUB 定向验证通过。
- 本轮提交：`fix: smooth reader chapter transitions`，仅包含 `MainWindow.xaml.cs`、`MainWindow.ReaderFeatures.cs`、`MainWindow.ReaderTools.cs`、`MainWindow.ReaderAi.cs`、`MainWindow.xaml`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 27. 目录跳转/章节切换闪现与短暂卡顿修复（2026-08-07）

### 根因（真实 EPUB 实测路径，非推测）

用户反馈点击目录跳转时仍先把旧章节淡出/滑出，然后单 WebView2 经 `ReaderWebView.Source = target` 导航新 XHTML；新文档加载、排版 CSS、图片适配、批注/脚注/定位/分页吸附等一系列工作完成前，画面闪白或像卡死一会。定位到上一轮"章节平滑过渡"实现的三个问题：

1. **导航前先播"出"动画**：`NavigateReaderSourceAsync` 在 `ReaderWebView.Source = target` 之前把唯一 WebView 淡出到 0.2 或左右滑出（130ms），旧内容提前不可见；WebView2 导航期间画面只剩 0.2 透明度的旧页或空白，看起来像卡死。
2. **`NavigationCompleted` 把非首屏工作全部串行 await 在入场动画之前**：批注还原（`ApplyReaderAnnotationsToPageAsync`）、脚注 hover（`ConfigureReaderFootnoteHoverAsync`，最多解析 120 个脚注目标）、定位（批注/搜索片段/书签/断点）、进度刷新等全部完成后才播放"入"动画，期间画面长时间空白。
3. **没有导航序列守卫**：快速连续点击目录时，被取代的旧导航的 `NavigationCompleted`（`IsSuccess=false`）会消耗最新章节的入场动画、提前释放过渡守卫，旧章节的延迟任务也可能覆盖新章节。

### 本次改动（时序、拆分、守卫；未动标题栏/无关 UI）

`src/Kkindle.App/MainWindow.xaml.cs` + `MainWindow.ReaderFeatures.cs` / `ReaderTools.cs` / `ReaderAi.cs`：

1. **过渡时序重排**：
   - 删除导航前的"出"动画：导航期间当前章节保持完全可见，`NavigateReaderSourceAsync` 只是设置 `_readerTransitionActive` 守卫并 `ReaderWebView.Source = target`，不再等 130ms 淡出。
   - `NavigationCompleted` 确认这是最后一次请求的导航（`_readerPendingNavigationTarget` 与当前 `Source` 一致）后：若有待入动画则先把宿主 `ReaderWebViewHost.Opacity = 0`（保持不透明白底，永不黑屏），跑首屏准备，就绪后再播放 180ms 淡入/滑入（`AnimateReaderPageTurnAsync` 入动画 180ms，落在 120–220ms 目标区间）；无动画模式立即显示。
   - `AnimateReaderPageTurnAsync` 增加 `style` 参数（1=淡入，2=滑入），入动画显式设置起始态（淡入 Opacity=0/Scale=0.985，滑入 TranslateX=±宽），不再依赖上一轮遗留的出动画结束态。
2. **首屏/非首屏任务拆分**：
   - 首屏（显示前必须完成）：`ApplyReaderAppearanceAsync()`（排版 CSS/视口/图片 contain 适配/分页吸附 `SnapReaderPaginationAsync`）、目标位置恢复（`ScrollToPendingReaderAnnotationAsync`/`ScrollToPendingReaderChunkAsync`/`ScrollToPendingReaderBookmarkAsync`/`ApplyReaderRestorePositionAsync`）、`MoveReaderToEndAsync`（`_readerNavigateToEnd`，只在本导航仍为当前时清除）、`PrimeReaderScrollEdgesAsync()`。
   - 非首屏（显示后执行）：新增 `RunReaderPostNavigationWorkAsync(sequence, token)`，fire-and-forget，顺序执行 `RetryReaderImageFitAsync(sequence)`（延迟重试带序列守卫）、`ApplyReaderAnnotationsToPageAsync`、`ConfigureReaderFootnoteHoverAsync`、`RefreshReaderProgressAsync`、`SaveReaderProgressThrottledAsync`、`SkipShortChapterIfNeededAsync`；每个 await 后检查 `IsStaleReaderNavigation(sequence, token)`（`_readerCloseRequested` || token 取消 || 序列号已变），旧章节/关闭的后置任务立即中止，异常隔离（catch 吞掉，绝不崩进事件循环）。
3. **所有导航路径一致 + 跳转语义**：目录点击（`ReaderTocList_SelectionChanged`）、书签（`NavigateToReaderBookmark`）、批注（`NavigateToReaderAnnotation`）、搜索片段（`NavigateToReaderChunkAsync`）、AI 来源（`ReaderAiSourceButton_Click`）、进度条跳转（`ReaderProgressSlider_ValueChanged`）一律传 `jump: true` → 即使动画菜单选"左右滑动"也播放短淡入，避免远章节跳转看起来像逐页拖动；上一/下一章（`TurnReaderPageAsync` 章界）、滚动接章（`PollReaderScrollAsync`）、分页跨章保持原滑动/仿真。待入动画以 `ReaderTurnInAnimation(Direction, Style)` 在导航开始时固定，不随中途改菜单而变化。
4. **导航守卫与失败路径**：
   - `_readerPendingNavigationTarget`：只有最后一次请求的导航允许走首屏/入场/后置流程；被取代的旧导航 `NavigationCompleted` 直接返回（不跑准备、不消耗入场动画）。
   - `!args.IsSuccess` 不再提前清守卫/清入场动画：被取消/取代的导航绝不能释放过渡守卫或吃掉最新章节的动画；守卫由最新章节的成功 `NavigationCompleted` 或 3 秒看门狗释放。每次导航（含无动画模式）都武装看门狗，失败导航不会永久卡住滚动轮询/分区点击。
   - `RetryReaderImageFitAsync` 接收导航序列号，延迟 250ms/700ms 的回调在旧章节或关闭时中止，不会给新章节补错封面类。
5. **关闭流程不受影响**：`CloseReader()`/`MainWindow_Closed()` 仍第一时间取消章节过渡令牌、清 `_readerPendingTurnInAnimation`/`_readerPendingNavigationTarget`、复位变换；后置任务因令牌/关闭标志立即中止，X 点击非阻塞顺序保持。未做双 WebView 相邻章节预加载（避免新架构引入更多卡死风险）。

### 定向验证（真实 EPUB《規模》31 章，Release 发布版 + UIA + 正文区域暗度采样）

- **目录远章节跳转（默认"仿真"淡入，至少 3 次）**：13→1（封面）、1→12（05 部分首页）、12→13（1. 生活在…正文）、13→1 共 4 次跳转全部通过。逐次记录：章节进度文本正确更新（13/31→1/31→12/31→13/31→1/31）；正文区域暗度（全白/冻结代理指标）无长时间全白——正文章节最长为入场淡入产生的约 234ms，无 1 秒级空白；文字正文章节最终暗度 0.10（正文可见），稀疏章节（部分标题页）最终暗度 0.0038 为真实内容而非冻结。开发期临时加入的 `nav.log` 计时（发布前已移除）确认 app 侧每次跳转 REQ→`NavigationCompleted`→首屏准备→入场完成 = **200–450ms**。
- **上一章/下一章**：从第 1 章（封面）点"下一页"1 次跨到第 2 章（进度 1/31→2/31），正文可见（finalDark=0.0202），跨章短过渡正常。
- **目录连续快速点击**：快速依次点 封面→05 部分→1. 生活在（选中成功的轮次）最终只显示最后一次目标（已读 13/31），无旧导航/旧后置任务覆盖，无全白/假卡死。（UIA 对虚拟化 TOC 项的查找偶发选不中，属测量工具限制，已用 `ItemContainerPattern` 缓解；"最终只显示最后一次目标"以全部选中的轮次为准。）
- **关闭验证**：点击 Kreader 顶部 X，阅读器面板 123–133ms 内移除，返回书架（"开始阅读"重新可见），进程存活响应。关闭不被动画/后置任务阻塞。

### 自动化边界（未验证项，需人工复核）

- 与前几轮一致，无法把真实鼠标/滚轮可靠投递到 WebView2 合成岛，**滚动自动接章、分页分区点击跨章**的端到端输入未在自动化中触发；其代码路径与已验证的目录/按钮跨章完全一致（`ShowReaderChapterAsync`/`NavigateReaderSourceAsync`）。
- 屏显时序用正文区域暗度采样（200ms 粒度）验证，动画观感（淡入曲线、有无闪烁）建议人工在交互桌面翻几页确认一次。
- 自动化环境曾出现"快速目录连续点击时个别 TOC 项选不中"，以及重负载全帧捕获（25ms/帧 BitBlt + 逐像素分析）会拖慢 WebView2 渲染导致"10 秒全白"假象；改用 200ms 轻量采样后确认真实切换为 200–450ms。后续人工验收无需处理此问题。

### 构建、测试与发布

- Debug/Release x64 完整解决方案构建：0 警告、0 错误；33 项测试（Debug/Release）全部通过。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新，13:44），Release 启动存活 + 真实 EPUB 定向验证通过。
- 本轮提交：`fix: reduce reader chapter switch jank`，仅包含 `MainWindow.xaml.cs`、`MainWindow.ReaderFeatures.cs`、`MainWindow.ReaderTools.cs`、`MainWindow.ReaderAi.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 28. 目录跳转定位修复：明确导航意图与章节首屏对齐（2026-08-07）

### 根因（真实 EPUB《規模》31 章 + Release/Debug 实测路径，非推测）

用户反馈点击左侧目录跳转到章节后，正文没有从新章节第一行顶格开始，仍保留旧章节/旧位置偏移，或目标章节首屏没有对齐到正文顶部。定位到旧实现四个问题：

1. **目录跳转没有明确导航意图**：`ReaderTocList_SelectionChanged` 只设 `_readerChapterIndex` 后调 `NavigateReaderSourceAsync`，`NavigationCompleted` 无条件依次执行批注/搜索/书签/断点恢复的待定位滚动。任何残留的 pending 位置（上一次打开保存的 `_pendingReaderRestorePosition`、被取代的搜索/书签/批注 pending）都可能被新的目录章节消费，把新章滚到旧位置。
2. **同章节目录点击是空操作**：`NavigateReaderSourceAsync` 对 `sameChapter` 直接 return，点当前章节既不能回顶也不能定位。
3. **`_readerNavigateToEnd` 泄漏**：上一章翻页设置 `_readerNavigateToEnd=true` 后若被目录点击取代，旧的"移动到章末"意图会覆盖目录目标，把新章滚到章末。
4. **首屏位置依赖浏览器默认行为**：新文档加载后没有显式把滚动容器复位到章节开头；若 WebView2 恢复旧滚动位置或目标 URL 带 fragment，首屏就不在正文顶部。

### 本次改动（定位意图 + 章节首屏对齐；未动标题栏/无关 UI）

1. `src/Kkindle.Core/ReaderModels.cs` — 新增 `ReaderNavigationIntent` 枚举与 `ReaderNavigationLocationPolicy`：`None`（打开书籍恢复/上一页下一页/滚动接章）、`Toc`（目录）、`Progress`（进度条）、`Bookmark/Annotation/Search/AiSource`（命名定位）。策略纯函数判定"哪些 pending 位置属于本次导航"。
2. `MainWindow.xaml.cs` — `NavigateReaderSourceAsync` 增加 `intent` 参数，导航开始前 `PruneReaderPendingLocations(intent)`：只保留属于本次导航的 pending（搜索/AI 保留 chunk，书签保留 quote，批注保留 annotation，打开书籍保留 restore），其余全部清空，杜绝旧导航/旧进度/旧后置任务覆盖新目标。
3. `MainWindow.xaml.cs` — `NavigationCompleted` 首屏位置改为 `ApplyReaderNavigationLocationAsync(intent, target)`：
   - `Toc` 无 fragment / `Progress`：`ResetReaderToChapterStartAsync()` —— 滚动模式 `window.scrollTo({left:0, top:0})`（正文自带顶部内边距，第一行从视口顶部内边距开始，不贴窗口边框）；分页模式先把 `scrollTop` 钉 0，再 `SnapReaderPaginationAsync()` 吸附到第一列边界 `paddingLeft + 0×视口`（实测 24px）。
   - `Toc` 带 fragment：`ScrollToReaderFragmentAsync()` 跳到锚点（id/name 查找 + `scrollIntoView`），不再统一回顶。
   - `Bookmark/Annotation/Search/AiSource`：各自原定位方法。
   - `None`：仅打开书籍的 `ApplyReaderRestorePositionAsync()`（未武装则为空操作），上一页/下一页/滚动接章/进度恢复不受影响。
4. `ReaderTocList_SelectionChanged` 传 `ReaderNavigationIntent.Toc` 并清 `_readerNavigateToEnd`；进度条跳转传 `Progress`；书签/批注/搜索/AI 来源分别传各自 intent。
5. **同章节目录点击**：`NavigateReaderSourceAsync` 的 `sameChapter` 分支不再空操作，改调 `RunSameChapterLocationAsync`（定位 + `PrimeReaderScrollEdgesAsync` 重对齐边沿，防止连续滚动轮询把"复位到顶"误判为"滚到顶"而弹回上一章）。
6. 保留：`WebView2 IsScriptEnabled=false`、EPUB 路径白名单、三栏布局、禅模式、翻页动画、分页/滚动模式、搜索/书签/批注/AI 定位、视口自适应与关闭流程。

### 真实定向验证（真实 EPUB《規模》31 章，Release 发布版 + Debug 实测 DOM 数值）

用 UIA 真实点击目录 + 生产代码路径翻页（键盘左右键、分页右 2/3 真实鼠标点击），逐次读取 `document.scrollingElement` 的 `scrollTop/scrollLeft`、章节 URL 与章节计数：

- 滚动模式目录跳转（3 次以上）：`intent=Toc` 后 `01.xhtml` → `st=0 sl=0`；`chapter05.xhtml` → `st=0`；正文首屏从第一行顶格开始（正文自带 `padding-top` 保留设计内边距）。
- 分页模式目录跳转：`chapter05.xhtml` → `sl=24 st=0`（第一列边界 = `paddingLeft + 0×视口`）；`01.xhtml` → `sl=24 st=0`；不继承上一章分页列。
- 读到中间再点目录（不继承旧位置）：滚动模式先 4 次生产翻页把 `05.xhtml` 滚到 `st=2160`，再点目录（同一章）→ `st=0`；分页模式 4 次真实右键点击把 `01.xhtml` 翻到 `sl=1857`，再点目录 `chapter01.xhtml` → `sl=24 st=0`。旧偏移不再继承。
- 快速连续点击目录：最终只显示最后一次目标（`chapter01.xhtml`，已读 4/31），且 `st=0`；旧导航/旧后置任务未覆盖。
- 带 fragment 的 TOC 锚点：点"2. 我们生活在一个呈指数级不断扩张的世界中"（`01.xhtml#sigil_toc_id_1`）→ `sl=7352`（锚点所在列），不是统一回顶，明确锚点仍跳锚点。
- 上一页/下一页跨章：`intent=None` 新章从顶部开始（`st=0`），回退章末逻辑不变；打开书籍进度恢复仍生效（恢复到章节 7/31 等保存位置）。
- Release 发布版 smoke：打开真实 EPUB 恢复进度、目录点击"05 从人类世到城市世"→ 已读 12/31，进程存活响应。

### 自动化边界（未验证项，需人工复核）

- 与前几轮一致，自动化无法把真实滚轮可靠投递到 WebView2 合成岛，滚动自动接章未端到端复跑；本次验证用生产代码路径（`TurnReaderPageAsync`、真实鼠标钩子分区点击、键盘左右键）驱动真实 DOM 滚动，并记录真实 `scrollTop/scrollLeft` 数值。
- 分页页内 `page-turn` 记录的是平滑滚动中值（`sl=482/940/1399/1857`），最终由 `SnapReaderPaginationAsync` 吸附到列边界；目录跳转后的位置是吸附后的精确值（`sl=24`）。
- 竖排滚动模式目录跳转未专项复跑（与横排走同一 `window.scrollTo({left:0,top:0})` 路径）；建议人工在交互桌面竖排模式点一次目录确认。
- 自动化环境 UIA 无法枚举阅读器底部上一页/下一页按钮（WebView2 合成岛相关的 UIA 剪枝），翻页改由键盘左右键与分页鼠标钩子分区点击驱动；两种路径都复用生产 `TurnReaderPageAsync`。

### 构建、测试与发布

- Debug/Release x64 完整解决方案构建：0 警告、0 错误；60 项测试（Debug/Release）全部通过。新增 `tests/Kkindle.Tests/ReaderNavigationLocationTests.cs`（27 项）：意图分类（哪些目标回章节首行）、pending 位置保留/清理（chunk/quote/annotation/restore 只属于各自意图）、TOC fragment 锚点判定。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新，14:23），Release 启动存活 + 真实 EPUB 目录跳转验证通过。
- 本轮提交：`fix: reset reader position on toc navigation`，仅包含 `MainWindow.xaml.cs`、`MainWindow.ReaderFeatures.cs`、`MainWindow.ReaderTools.cs`、`MainWindow.ReaderAi.cs`、`Core/ReaderModels.cs`、`tests/Kkindle.Tests/ReaderNavigationLocationTests.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 29. 目录跳转首行归一化：消除 EPUB 首元素边距/空白节点造成的"不顶格"（2026-08-07）

### 根因（用真实《規模》31 章 + 生产注入 CSS/生产归一化脚本在 WebView2 复现工具中读取真实 DOM，非推测）

用户反馈：上一轮已把普通目录跳转的滚动位置设为 `scrollTop=0`、分页设为 `scrollLeft=24/top=0`，但**有的章节还是没有顶格**。本轮用生产注入 CSS + 生产归一化脚本加载真实《規模/Scale》EPUB 全部 31 章逐章读取 `document.scrollingElement.scrollTop/scrollLeft`、`body` paddingTop、首元素 `getBoundingClientRect().top/left`、computed `marginTop/paddingTop`、是否空白 `br/p/div`、首个标题/段落/图片/SVG 尺寸，确认根因：

1. **`scrollTop=0` 只复位滚动容器，不能消除"首元素自身的上边距"**。真实《規模》EPUB 样式表（`OEBPS/Styles/stylesheet.css`）有 `div.chatu-part { margin-top: 30% }`，本书的部页/致谢/注释开篇（`chapter01–10.xhtml`、`thanks.xhtml`、`postscript.xhtml`、`reference.xhtml`、`introduction.xhtml`）首元素都是 `<div class="chatu-part"><img …/></div><h1>…</h1>`。生产注入 CSS 只覆盖了 `p`/`h1–h4` 等元素边距，不会覆盖 `.chatu-part` 的 30% 边距。实测（视口 1053×796，滚动模式）：`div.chatu-part` computed `marginTop=199.2px`（30% × 正文内容宽 664），首个可见内容（图片）`rectTop=257.2`，而不是正文内容盒设计起始线（body paddingTop=58px）；分页模式 `marginTop=233.275px`（30% × 列宽 777.6），**章节标题 `h1` 被推到第二列**（`firstHeading.left=833.6`），第一页只有图和大片空白。
2. **首元素是普通标题/段落时也有"非零首行边距"**。`01/05/06.xhtml` 等以 `<h2>` 开头、`02–04/07–10.xhtml` 等以 `<p>` 开头、`copyright.xhtml` 以 `<h1 class="bqbt2">` 开头。生产 CSS 的 `h1–h4 { margin:1.35em … !important }` / `p { margin:0.55em … !important }` 已把 EPUB 的 inline `margin-top:30%` 覆盖掉，但保留的 1.35em（实测 23.76px）/0.55em（8.8px）/29.8px 首行边距仍让第一行落在设计起始线之下（如 `01.xhtml` 滚动模式 `h2.rectTop=81.75`，设计线为 58）。
3. **首行空白节点/空块**：章节开头普遍有换行缩进文本节点、空 `<p>`/`<br>`（归一化脚本实测每章移除 1 个以上前导空白节点），会额外增加首屏空白。
4. **封面章节不受影响但需保持**：`cover.xhtml` 首元素 `<div style="margin:0pt">` 本身无上边距，SVG 封面由既有图片 fit 处理（分页 361×481 在内容盒内、top 84.8），归一化对其无副作用、不能破坏封面适配。

结论一句话：**scrollTop/scrollLeft 复位之外，首元素自带的 margin/空白节点才是"不顶格"的根因**；只能对章节开头的空块和第一个有效内容元素做安全归一化，不能删除全书排版，也不能动带 fragment/搜索/书签/批注/AI 的定位。

### 本次改动（定位策略 + 章节开头安全归一化；未动标题栏/无关 UI）

1. `src/Kkindle.Core/ReaderModels.cs` — 新增 `ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(intent, target, hasPendingRestorePosition)` 纯函数：普通目录项（无 fragment）、进度条跳转、以及无断点恢复的普通章节切换（None）需要归一化；带 fragment 的目录锚点、书签/批注/搜索/AI 来源一律不归一化（它们有自己的精确滚动目标，且搜索/AI 的字符偏移依赖未改动的 DOM）。
2. `src/Kkindle.App/MainWindow.xaml.cs` — `ResetReaderToChapterStartAsync()` 在滚动复位前先调用新增的 `NormalizeReaderChapterStartAsync()`（执行 `GetReaderChapterStartNormalizationScript()`）：
   - 只删除章节开头的**空白块**（纯空白文本节点、空 `br/p/div`、只含空白内容的包裹元素）；含 `img/svg/picture/table/hr` 等可视元素或含 `[id]` 锚点的元素**永不删除**（封面页与 fragment 目标安全）。
   - 把**第一个有效内容元素**的 `margin-top` 以 inline `!important` 归零（优先级高于 EPUB 内联样式和生产注入样式的 `!important`）；若首元素是普通包裹（div/section 等），再下钻把内部第一个文本块（p/h1–h6/li/blockquote 等）的 `margin-top` 归零以消除 margin 穿透。
   - 正文段落内部间距、标题层级、列表/表格样式、图片/封面 viewport fit 全部保留；同一章节重复点击幂等。
3. `MainWindow.xaml.cs` — `ApplyReaderNavigationLocationAsync` 的 `None` 分支：`_pendingReaderRestorePosition` 为空时改走 `ResetReaderToChapterStartAsync()`（原来为空操作，现在普通上一章/下一章/滚动接章也归一化首行）；有断点恢复时仍走 `ApplyReaderRestorePositionAsync()`，绝不归一化。
4. 保留：`WebView2 IsScriptEnabled=false`、EPUB 路径白名单、三栏布局、禅模式、翻页动画、分页/滚动模式、搜索/书签/批注/AI 定位、导航序列守卫（快速连续目录点击仍只有最后一次导航的后置任务能改位置）、视口自适应与关闭流程。

### 真实定向验证（真实《規模》31 章，WebView2 复现工具 + 生产 CSS/归一化脚本逐章读取 DOM；Release 发布版 UIA 端到端）

抽样 11 类章节 × 滚动/分页两个模式，读取 `scrollTop/scrollLeft`、body paddingTop、首元素 rect/computed margin、首个标题/段落/图片/SVG 尺寸（视口 1053×796，与阅读正文宿主一致）：

| 章节类型 | 示例 | 修复前（scrollTop=0 后首元素/首可见内容） | 修复后 |
|---|---|---|---|
| SVG 封面 | cover.xhtml | div marginTop 0，svg top 84.8（fit） | 不变（归一化无副作用，封面 fit 保持 361×481 分页） |
| 图片开篇（chatu-part） | chapter01/02…10、reference、thanks、postscript、introduction | 滚动：div marginTop **199.2px**、图片 rectTop **257.2**；分页：marginTop **233.275px**、h1 标题被推到第二列（left 833.6） | 滚动：marginTop **0**、图片 rectTop **86.8**（58+图片 1.8em fit 边距）、h1 top 401.84；分页：标题回到第一列（left 32）、图片 top 84.8 |
| 标题开篇 | 01/05/06.xhtml、Section0002/0003 | h2 marginTop 23.76px，h2 rectTop 81.75（滚动）/79.75（分页） | h2 marginTop **0**，h2 rectTop **58**（滚动）/ **56**（分页=48+标题自身 padding/border） |
| 段落开篇 | 02/03/04/07–10.xhtml、Section0001/0004 | p marginTop 8.8px，p rectTop 66.8（滚动）/64.8（分页） | p marginTop **0**，p rectTop **58**（滚动）/ **56**（分页） |
| 版权页 | copyright.xhtml | h1 marginTop 29.8px，h1 rectTop 87.8 | h1 rectTop **58** |
| 包裹开篇 | dedication.xhtml（div.juzhong3 内含 p/img） | div marginTop 199.2px，div rectTop 257.2 | div marginTop 0、内层首 p 也归零，div rectTop 86.8 |

- **读到中间再点目录（不继承旧位置）**：先程序化滚动到章节中部（滚动 45% 高度 / 分页 45% 列宽）再执行章节首行导航——修复后 `scrollTop=0`/`scrollLeft=24`，首元素 rectTop 回到设计线，不继承旧偏移；修复前仅滚动复位时首元素仍带 199px/233px 边距。
- **分页第一列吸附**：`SnapReaderPaginationAsync` 在归一化后仍把 `scrollLeft` 吸附到第一列边界（短章节内容一列放得下时钳到 0，即第一列），`scrollTop=0`。
- **Release 发布版 UIA 端到端**：启动存活、打开真实 EPUB（恢复到上次进度）、目录搜索框（Name=“搜索目录”）逐项搜索+点击真实 TOC 条目（封面/1. 简介/2. 误导/3. 生命/注释 等）章节计数均变化、过渡无冻结；**快速连续 3 次目录点击最终只落在最后一次目标且 2 秒后计数稳定**；X 关闭 185–190ms、进程存活。
- 单元测试：Debug/Release 各 **70 项全部通过**（新增 4 项：`ShouldNormalizeChapterStart` 只在普通章节目标执行、TOC 带 fragment 跳过、断点恢复跳过、null 目标安全）。

### 自动化边界（未验证项，需人工复核）

- 与第 21/22/24/25 节一致：本会话无法把真实鼠标/滚轮可靠投递到 WebView2 合成岛，因此"真实滚轮滚到中部再滚回顶"未端到端复跑；DOM 位置验证用生产 CSS + 生产归一化脚本在 WebView2 中加载真实章节文件实测，数值属真实渲染结果。
- 归一化脚本对"首元素是表格/列表/大段文字"等章节的视觉观感未做像素级验收（首元素 margin 归零、内部结构不变，理论上安全）；建议人工在交互桌面翻到 `chapter05.xhtml`（部页）、`01.xhtml`、`cover.xhtml` 各看一眼。
- 竖排滚动模式目录跳转未专项复跑（与横排走同一 `window.scrollTo({left:0,top:0})` + 归一化路径）；建议人工在竖排模式点一次目录确认。

### 构建、测试与发布

- Debug/Release x64 完整解决方案构建：0 警告、0 错误；70 项测试（Debug/Release）全部通过。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新，14:52），Release 启动存活 + 真实 EPUB 目录跳转/快速连续点击/关闭验证通过。
- 本轮提交：`fix: normalize reader chapter start`，仅包含 `MainWindow.xaml.cs`、`Core/ReaderModels.cs`、`tests/Kkindle.Tests/ReaderNavigationLocationTests.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。

## 30. 子章节 fragment 跳转定位：标题顶格新列/正文内容盒顶部（2026-08-07）

### 根因（真实《規模》31 章 + 生产注入 CSS/生产 fragment 定位脚本在 WebView2 复现工具中读取真实 DOM，非推测）

用户反馈：阅读器左侧目录点击子章节（如 `5. 为何公司会衰亡，而城市则不会？`）后，正文页面顶部（y≈150）仍显示上一段正文（`株式会社金刚组，它于公元578年创办于日本的大阪…`），被选中的子章节标题停在正文页面中部（y≈405）。这不是普通章节 `scrollTop=0` 能解决的问题（上一轮已修复普通章节首行边距/空白节点）：**子章节是同一个 XHTML 内部的 fragment/TOC 锚点，旧实现 `ScrollToReaderFragmentAsync` 只做 `scrollIntoView({block:'start'})` + 分页吸附 `SnapReaderPaginationAsync`，`scrollIntoView` 只移动滚动容器、无法把目标标题移到 CSS 列顶部——当标题与前文共用一个 CSS column 时，无论 `scrollTop/scrollLeft` 怎么设，标题都停在列中部**。用真实数据确认：

1. **fragment 定位时机/列布局是根因，不是 fragment 丢失**。EPUB navigation parser（`EpubReaderPreparationService.CreateNavigationItems`）一直保留 `#fragment`（如 `Text/09.xhtml#sigil_toc_id_68`），`ApplyReaderNavigationLocationAsync` 也正确把带锚目录项路由到 `ScrollToReaderFragmentAsync`；问题在定位方法本身。
2. **分页模式实测（视口 1053×796，报告例 `09.xhtml#sigil_toc_id_68`）**：旧实现 `scrollIntoView` + 吸附后目标 `h2` `rectTop=256.38`（标题停在列中部）、`marginTop=23.76px`，同列上方就是上一段 `p`（`prevTop=56`）——正文顶部显示上一段、标题停中部，与用户截图完全一致；大窗口 1900×950 下 `rectTop=226.94`，仍停列中部。6 个真实子章节分页 `before.rectTop` 全部在 226.94–538.44（列中部），无一在内容起始线。
3. **滚动模式实测**：旧实现 `scrollIntoView` 把标题 `rectTop` 滚到 0.23/0.29/-0.37（标题贴窗口顶边，正文内容盒 paddingTop=58 完全丢失）。
4. **`break-before` 关键字陷阱**：为让标题从新列开始，先尝试 `.kkindle-fragment-break { break-before: column !important; page-break-before: always !important; }`，实测无效（`breakPage`/`pageBreakAlways` 探针 `rectTop` 仍 249.43 停在列中部）；只保留 `break-before: column !important` 后标题移动到新列顶部（探针 `rectTop=56`）。原因：旧别名 `page-break-before: always` 会级联覆盖 `break-before` 为 `page`，而 `break-before: page` 在 WebView2 的屏幕多列布局里不产生列断点。

### 本次改动（fragment 专属定位 + 分页临时列断点 + 安全回退；未动标题栏/无关 UI）

1. `MainWindow.xaml.cs` — 阅读器样式表新增两个一次性标记类：`.kkindle-fragment-break { break-before: column !important; }`（分页强制目标块从新列顶部开始）与 `.kkindle-fragment-zeroed { margin-top: 0 !important; }`（标题贴内容起始线）。**注释明确禁止再混入 `page-break-before`**。
2. `MainWindow.xaml.cs` — 重写 `ScrollToReaderFragmentAsync`，新增生产脚本 `GetReaderFragmentScrollScript(needle, flowMode, vertical)`：
   - 按 `id`/`name` 解析锚点；隐藏/空/缺失锚点向前找第一个有效标题/段落/图片（`valid()` 判 `display/visibility/零尺寸/文本`），找不到返回 `ok:false`；
   - 先清除上一次 fragment 跳转留下的断点/归零标记（快速连续子章节点击只保留最后一次目标的断点）；
   - 分页模式给目标块加 `.kkindle-fragment-break` + `margin-top:0 !important`，`void offsetHeight` 强制同步重排后计算目标列 document-left，`scrollTo({left: 列左边界, top:0})`（列边界 = `paddingLeft + n×clientWidth`，正是 `SnapReaderPaginationAsync` 的吸附网格，后置图片重试/重排不会把位置改回去）；滚动模式把标题 document-top 减去 body paddingTop（保留统一阅读内边距，不贴窗口边）；竖排写作按块首（右缘）对齐内容盒右缘；
   - 返回真实测量 JSON（`rectTop/rectLeft/scrollLeft/marginTop/column`）供宿主日志与验证。
3. `MainWindow.xaml.cs` — 宿主 `ScrollToReaderFragmentAsync`：脚本 `ok:false`（锚点完全缺失）安全回退 `ResetReaderToChapterStartAsync()`（章节首行归一化 + 复位），不崩溃；分页模式脚本定位后再 `SnapReaderPaginationAsync()` 幂等吸附，保证后置任务（图片 fit 重试 250/700ms、批注、脚注、进度）不把位置改回旧列。
4. `MainWindow.xaml.cs` — `GetReaderChapterStartNormalizationScript` 开头清理 `.kkindle-fragment-break`/`.kkindle-fragment-zeroed`：普通章节目录点击/上一页下一页把临时断点清掉，目标标题回到自然位置，**不改变书籍原始内容结构**（仅运行时 DOM，切章即丢弃）。
5. `src/Kkindle.Core/ReaderModels.cs` — `ReaderNavigationLocationPolicy.TocTargetHasAnchor/TocAnchorId` 加固：`.NET Uri.Fragment` 对结尾裸 `#` 返回 `"#"`，不再把它当成锚点（EPUB parser 本就不会产出空 fragment，防御性加固）。
6. 保留：fragment 意图优先于普通章节首行归一化/旧进度/批注/搜索定位（`PruneReaderPendingLocations` + 意图路由不变）、`WebView2 IsScriptEnabled=false`、EPUB 路径白名单、三栏布局、禅模式、翻页动画、分页/滚动模式、书签/搜索/批注/AI 定位、导航序列守卫、视口自适应与关闭流程。

### 真实定向验证（真实《規模》31 章，6 个子章节 fragment × 滚动/分页，WebView2 复现工具 + 生产 CSS/生产脚本读取真实 DOM；Release 发布版 UIA 端到端）

复现工具以生产 CSS（含 fragment 断点类）加载真实章节文件，按"旧实现（scrollIntoView+吸附）→ 新生产脚本 → 普通章节归一化"三个阶段读取目标 `h2` 的 `getBoundingClientRect`/`scrollTop/scrollLeft/clientWidth/clientHeight/marginTop`、目标列左边界与顶部元素。视口 1053×796（与阅读正文宿主一致），报告例另测 1900×950：

| 子章节（fragment） | 模式 | 修复前标题 rectTop（旧实现） | 修复后标题 rectTop | 目标列左边界 / scrollLeft | 顶部元素 |
|---|---|---|---|---|---|
| `09.xhtml#sigil_toc_id_68`（报告例"5. 为何公司会衰亡…"） | 滚动 | 0.23（贴窗口顶） | **58.08**（内容盒起始线=58） | — / 0 | `h2` |
| `09.xhtml#sigil_toc_id_68` | 分页 | 256.38（列中部，顶部上一段 prevTop=56） | **56**（新列顶部） | 0 / 36230（列边界） | `h2`（上一段在左前列 off-screen） |
| `09.xhtml#sigil_toc_id_68` @1900×950 | 分页 | 226.94 | **56** | 0 / 48632 | — |
| `01.xhtml#sigil_toc_id_1`（"2. 我们生活在一个呈指数级…"） | 滚动 | 0.29 | 58.14 | — / 0 | `h2` |
| `01.xhtml#sigil_toc_id_1` | 分页 | 538.44 | **56** | 0 / 8444 | — |
| `01.xhtml#sigil_toc_id_5`（"6. 规模缩放与复杂性…"） | 滚动 | -0.37 | 58.28 | — / 0 | `h2` |
| `01.xhtml#sigil_toc_id_5` | 分页 | 273.18 | **56** | 0 / 18548 | — |
| `02.xhtml#sigil_toc_id_10`（"3. 数量级、对数、地震…"） | 滚动 | -0.08 | 57.78 | — / 0 | `h2` |
| `02.xhtml#sigil_toc_id_10` | 分页 | 302.61 | **56** | 0 / 11812 | — |
| `03.xhtml#sigil_toc_id_24`（"6. 网络与1/4次幂…"） | 滚动 | -0.06 | 57.79 | — / 0 | `h2` |
| `03.xhtml#sigil_toc_id_24` | 分页 | 273.18 | **56** | 0 / 23600 | — |
| `05.xhtml#sigil_toc_id_38`（"2. 城市、城市化和全球可持续性"） | 滚动 | -0.37 | 58.28 | — / 0 | `h2` |
| `05.xhtml#sigil_toc_id_38` | 分页 | 517.33 | **56** | 0 / 5918 | — |

- **截图对应子章节（09.xhtml#sigil_toc_id_68）必须通过**：分页修复后正文顶部元素是目标 `h2`（`topElementTag=h2`），上一段 `p` 的 `prevLeft` 为负（在左前一个列、随滚动吸附后被移出视口），不再显示上一段；滚动模式 `h2` 在顶部且 `prevTop` 为负（上一段在视口上方）。标题全部顶格在正文内容区域顶部。
- **普通章节与带 fragment 章节互不影响**：普通目录项（无 fragment）仍走章节首行归一化 + `scrollTop=0`/第一列吸附（`sl=24/st=0`）；带 fragment 目录项走新 fragment 定位，两者路由互斥。
- **快速连续子章节点击只显示最后目标**：同章内先跳 `sigil_toc_id_67` 再立即跳 `sigil_toc_id_68`，最终 `breakCount=1`（只有最后一次目标的断点保留），`sigil_toc_id_67` 的断点被清除（`hasBreakClass=False`、rectTop 回到自然位置），`sigil_toc_id_68` 在新列顶部（rectTop=56、rectLeft=0、atTop=True）。
- **普通章节导航恢复原排版**：归一化脚本清断点后标题 `rectTop` 回到自然文档位置（`reset.headingTop` 与修复前相同），原书交错排版恢复，未改动书籍原始内容结构。
- **Release 发布版 UIA 端到端**：启动存活、打开真实 EPUB（恢复 21/31）、目录搜索框逐项搜索+点击真实 TOC 条目——子章节 fragment `frag-01-1`（01.xhtml#sigil_toc_id_1）21→5、`frag-02-10`（02.xhtml#sigil_toc_id_10）5→7、报告例 `frag-68`（09.xhtml#sigil_toc_id_68，同章 21/31）重定位不卡计数不变、普通章节 `plain-02-1` 同章计数不变；快速连续 3 次目录点击最终只落在最后一次目标且 2 秒后计数稳定；全程无冻结；X 关闭 190ms。
- 单元测试：Debug/Release 各 **74 项全部通过**（新增 4 项：fragment 锚点不归一化/不继承 pending 恢复、fragment 意图不吃旧 pending、结尾裸 `#` 视为普通章节）。

### 自动化边界（未验证项，需人工复核）

- 与第 21/22/24/25/29 节一致：本会话无法把真实鼠标/滚轮可靠投递到 WebView2 合成岛，因此"真实滚轮滚到中部再点子章节"未端到端复跑；DOM 位置验证用生产 CSS + 生产脚本在 WebView2 中加载真实章节文件实测，数值属真实渲染结果。
- 分页列的横向位置：目标标题所在列的内容左缘与 `SnapReaderPaginationAsync` 吸附网格（`clientWidth`）存在亚像素级差异（实测列宽 793.6px+48px gap vs 吸附步长 842px），随列号累计最多约 0.4px×列数（报告例 43 列 ≈17px）的横向偏移；这是既有分页网格本身的性质（普通翻页同样存在），本轮不改变吸附网格，标题始终在新列顶部、横向仍落在页面内，视觉无异常；未做像素级截图比对。
- 竖排滚动模式子章节 fragment 未专项复跑（脚本含竖排分支：块首右缘对齐内容盒右缘、行首顶部对齐内容盒顶）；建议人工在竖排模式点一个子章节目录项确认。
- 隐藏/空 fragment 回退（向前找第一个有效标题/段落/图片 → 找不到回退章节首行）已实现并有单元/集成路径，但真实《規模》所有 fragment 都指向有效 `h2`，该分支未被真实书触发；建议人工对一本锚点异常的 EPUB 抽验。

### 构建、测试与发布

- Debug/Release x64 完整解决方案构建：0 警告、0 错误；74 项测试（Debug/Release）全部通过。
- 标准 `publish` 目录已重新发布：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`（本轮更新，15:41），Release 启动存活 + 真实 EPUB 子章节 fragment 跳转/快速连续点击/关闭验证通过。
- 本轮提交：`fix: align subchapter navigation`，仅包含 `MainWindow.xaml.cs`、`Core/ReaderModels.cs`、`tests/Kkindle.Tests/ReaderNavigationLocationTests.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。
