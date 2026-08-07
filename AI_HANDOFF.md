# Kkindle 项目交接文档

> 给后续 AI / 开发者使用。继续工作前请先阅读本文档，再查看代码和当前 Git 状态。
>
> 更新时间：2026-08-07
>
> 项目目录：`C:\Users\kings\Desktop\01_Projects\Kkindle`

## 0. 当前状态速览

- 当前阶段：P0、P1 已完成；P2 自动化和真机大文件传输已完成；内置阅读器已完成三栏界面重设计，并在阅读助手中新增本地书库索引、AI 问答和划线/批注。本轮完成正文视口修复（正文不再一次铺满整章）后，继续修复了两个真实交互失效：连续滚动滚到章节底部不再自动进入下一章、分页模式点击正文右侧不再翻页。根因是 `IsScriptEnabled=false` 下 WebView2 冻结了页面事件派发，注入的 scroll/click 监听永远不触发；已改为宿主侧轮询滚动位置 + 低级鼠标钩子，真实运行路径验证通过。
- 当前分支：`master`；最新本地提交为 `1444cb9 fix: fit reader content to viewport`（详见第 21 节）。本轮修复提交见第 22 节。
- GitHub：本地领先 `origin/master` 多个提交，按开发约定未自动推送。
- 最新便携版：`src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`，exe 更新于 2026-08-07 本轮发布。
- 最新源码验证：Debug/Release x64 完整解决方案构建均为 0 警告、0 错误；22 项测试两个配置全部通过。Release 已重新发布并用真实 EPUB（《策略思维》）验收：滚动模式滚到章节底部自动进入下一章且新章从顶部开始、分页模式左 1/3 上一页/右 2/3 下一页（含跨章）、目录跳转、禅模式等既有功能未破坏。
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

当前工作分支为 `master`；`origin/master` 已与本地同步（2026-08-06 已推送），远端最新为 `4e8009b`。构建输出由 `.gitignore` 排除，不纳入版本控制。

当前开发基线及最近提交：

```text
4e8009b feat: add reader AI assistant, highlights, notes, and book index
abeb663 feat: redesign reader workspace
d17bcb3 test: complete P2 import and Kindle validation
bf36d34 feat: add reader flow modes and font sizing
5c88d6a feat: add reader table of contents and controls
f841cb1 feat: add minimal built-in book reader
43c5849 docs: update agent handoff after P1
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
- 正文阅读区视口规则（本轮修复）：WebView 宿主本身就是正文视口，宽高由目录/助手/窗口布局决定；滚动模式正文自然增长并在 WebView 内纵向滚动；分页模式用 CSS 多列按视口分页，`html { overflow: hidden }` 是唯一滚动容器，`body` 必须保持 `overflow: visible`，否则列溢出会被传播到视口裁切，出现"整章压进一屏"。
- 阅读区尺寸变化（窗口缩放、目录/助手收起、禅模式切换）会触发 `ReaderContentPanel_SizeChanged` → `ScheduleReaderRelayout()`（防抖 120 ms）重新应用视口样式并收敛滚动位置；分页翻页脚本固定 `top: 0`，防止纵向漂移。
- P1 已完成，P2 自动化场景已增至 18 项，Kindle Scribe 64 MiB 传输闭环也已通过。下一步只剩需要人工配合的物理拔出/重连；USB 磁盘安全弹出需等对应设备。不要再次重做标题栏架构，除非有新的可复现问题。

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
- 本轮提交：`fix: repair reader page interaction`，仅包含 `MainWindow.xaml.cs`、`MainWindow.ReaderFeatures.cs`、`AI_HANDOFF.md`；未提交构建输出、`.opencode/` 与既有未提交的 `App.xaml` 改动；未 push/amend。
