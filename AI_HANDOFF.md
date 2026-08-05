# Kkindle 项目交接文档

> 给后续 AI / 开发者使用。继续工作前请先阅读本文档，再查看代码和当前 Git 状态。
>
> 更新时间：2026-08-05
>
> 项目目录：`C:\Users\kings\Desktop\01_Projects\Kkindle`

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
- MTP 发送已接入 Windows Shell：先复制为 `.kkindle-part`，轮询设备端大小，校验完成后改为正式文件名；同名文件自动编号且不覆盖。
- 发送前显示目标设备和不覆盖规则，发送完成后自动刷新设备书籍；真实 Scribe 大文件传输与拔线中断仍待手工验收。
- MTP 删除和弹出仍显式禁用；传统 USB 磁盘流程不受影响。

当前 UI 主要使用 3 秒一次的设备轮询。`NativeDeviceChangeMonitor.cs` 还在项目中，但 WM_DEVICECHANGE 接入尚未作为稳定主流程验收。

### 3.5 测试

当前已有 4 个测试，并在 2026-08-05 复核通过：

- EPUB 导入、元数据、封面和哈希去重。
- 标题/标签搜索。
- 临时目录模拟 Kindle，发送和扫描书籍。
- 同名不同内容发送时生成编号文件且不覆盖原文件。

复核命令和结果：

```text
dotnet build Kkindle.sln -c Debug -p:Platform=x64 --no-restore
0 个警告，0 个错误

dotnet test Kkindle.sln -c Debug -p:Platform=x64 --no-build
失败 0，通过 3，跳过 0
```

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
- `ProbeWindow.xaml` / `ProbeWindow.xaml.cs` 是临时诊断文件，问题解决后删除。
- 当前 `App.xaml` 只有自定义颜色和按钮样式，尚未稳定加入 WinUI 默认控件资源。
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

### P1 恢复可用 UI

- [x] 恢复黑白墨水屏风格、零圆角硬边矩形的完整 MainWindow。
- [x] 恢复书架卡片封面、标题、作者和格式信息。
- [x] 区分本地书库与 Kindle 书库，并加入封面悬浮详情。
- [x] 按书籍管理、设备管理、阅读资料和系统重组左侧导航，并加入设备连接与容量状态卡。
- [x] 恢复搜索、列表/网格切换和书籍详情面板。
- [ ] 验证空书库、无封面、长标题和高 DPI。
- [ ] 用真实导入的 EPUB 做一次完整手工验收。

### P1 完善 Kindle 流程

- [x] 设备页显示 USB 磁盘与 WPD/MTP Kindle 的设备书籍和容量。
- [x] 发送前显示目标设备和同名不覆盖规则。
- [x] 发送完成后刷新设备书籍列表。
- [ ] 只删除 `documents` 下目标文件，并完善二次确认。
- [ ] 验证设备拔出、复制中断、无权限和盘符变化。
- [ ] 评估是否重新接入 WM_DEVICECHANGE；轮询可以作为可靠兜底。

### P2 补充测试

- [ ] 损坏文件逐项失败且不影响其他文件。
- [ ] 中文文件名和中文元数据。
- [ ] 文件名清理和同名冲突。
- [ ] SHA-256 校验失败后的临时文件清理。
- [ ] 取消导入和取消发送。
- [ ] 同名不同内容发送到 Kindle 时自动编号。
- [ ] 设备盘符变化和 `documents` 路径安全边界。
- [ ] 真实 Kindle 插拔、弹出和大文件传输。

## 7. 不要做的事情

- 不要读取或修改 calibre 数据库。
- 不要访问 Kindle 的 `system` 目录或内部数据库。
- 不要覆盖 Kindle 上内容不同的同名文件。
- 不要因为修 UI 而改变 SQLite 数据结构，除非先更新迁移策略。
- 不要把单个导入失败升级为整批失败。
- 不要在启动问题未解决前继续扩展格式转换、阅读器、同步服务等范围外功能。

## 8. Git 状态

仓库已建立首个“可启动的 WinUI 最小版本”提交；构建输出由 `.gitignore` 排除，不纳入版本控制。

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
