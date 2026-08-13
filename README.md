# Kkindle

[![Release](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml)
[![GitHub release](https://img.shields.io/github/v/release/kingstacker/Kkindle?display_name=tag)](https://github.com/kingstacker/Kkindle/releases/latest)

Kkindle 是一款面向 Windows 11 的个人电子书与 Kindle 设备管理器。它使用 WinUI 3 构建，将本地书库、格式转换、阅读、批注、AI 辅助阅读和 Kindle 传输集中在一个简洁的灰白纸张风格界面中。

## 界面预览

### 电脑书库（书架视图）

![Kkindle 电脑书库主界面](docs/images/主界面.png)

### 电脑书库（画廊显示）

![Kkindle 画廊显示模式](docs/images/画廊显示.png)

### 电脑书库（右键菜单）

![Kkindle 书库右键菜单](docs/images/电脑书库右键菜单.png)

### Kindle 书库管理

![Kkindle 设备书库管理](docs/images/Kindle书库管理.png)

### Kindle 字体与字典管理

![Kkindle 字体与字典管理](docs/images/Kindle字体字典管理.png)

### 阅读资料（划线批注）

![Kkindle 划线批注管理](docs/images/Kindle划线批注管理.png)

### Kreader 阅读器

![Kreader 分页阅读界面](docs/images/Kreader阅读界面.png)

### Kreader 禅模式

![Kreader 禅模式阅读界面，左侧为极简目录](docs/images/禅模式阅读界面，左侧为极简目录.png)

### Z-Library 在线书库

![Z-Library 在线书库](docs/images/Z-library在线书库.png)

### 基础设置

![Kkindle 基础设置界面](docs/images/基础设置界面.png)

## 主要功能

### 本地书库管理

- 导入 EPUB、PDF、MOBI 和 AZW3，支持拖放导入与中文文件名；导入文件夹时可逐本选择是否自动补齐 EPUB/AZW3 格式。
- 自动解析 EPUB 的标题、作者、简介和封面，使用 SHA-256 去重，并把同一本书的不同格式统一归档。
- 提供标题/作者搜索、作者/标签/格式/分类/阅读状态筛选、收藏筛选和多种排序方式。
- 支持分类、收藏、待读/阅读中/已读状态管理；开始阅读和读完时会自动更新状态。
- 提供书架、列表和画廊显示模式，支持封面、标题、作者、系列、标签、分类、简介等元数据编辑，以及框选/多选批量操作（发送到 Kindle、发送到邮箱、删除）。
- 使用独立 SQLite 数据库，书籍、封面和阅读记录均保存在本机。

### Kreader 阅读器

- 阅读 EPUB、PDF、AZW3 和 MOBI；打开 AZW3/MOBI 时会自动准备临时 EPUB 阅读副本。
- EPUB 支持目录、书签、页内查找（Ctrl+F）和阅读进度记忆，横排或竖排分页/滚动阅读、双页显示，并会预加载下一章减少切换等待。
- 翻页动画支持无动画、淡入淡出、左右滑动和水波流动；禅模式提供真全屏阅读（F11 进入、Esc 退出），支持极简目录。
- PDF 支持本地文本索引、全文搜索、页码进度、书签、页面笔记和 AI 上下文检索。
- 支持字号、行高、正文宽度、页边距和 CJK 字体等按书保存的排版设置，也可配置新书默认排版。
- 支持 EPUB 划线、笔记、批注定位与导出，阅读资料仅保存在本机。
- 内置 AI 阅读助手，可基于当前选文和本地书籍索引对话，支持思考深度与模型选择，并兼容 DeepSeek、OpenAI 等接口。

### 阅读效率工具

- 导入 TTF、OTF、WOFF 或 WOFF2 字体，并在阅读排版和默认排版中直接选择。
- 导入 UTF-8 文本词典（`词条<Tab>释义` 或 `词条=释义`），阅读时选词即可查询。
- 阅读数据看板汇总已开始/已读完书籍、累计时长、平均进度、书签和批注，并支持导出 CSV。
- “笔记与标注”统一汇总全部本地书籍的划线批注和已连接 Kindle 的 `My Clippings.txt`；支持来源筛选、全文搜索、本地原文定位及逐条删除。
- “导出记录”可按当前来源与搜索条件，将本地和 Kindle 阅读资料合并导出为 Markdown 或纯文本。

### Z-Library 在线书库

- 通过 Z-Library 官方 eapi 搜索并下载书籍，支持书名/作者搜索、格式与语言筛选、分页浏览。
- 下载完成后自动导入电脑书库，自动解析元数据与封面并去重；临时下载文件自动清理。
- 账号凭据（邮箱与密码）使用 Windows 当前用户加密保存在本机，不写入备份包；API 服务地址可配置，便于在官方域名不可用时切换到可用镜像。
- 下载任务在列表中实时显示状态，可随时取消，完成后自动入库。

### 格式转换

- 通过 Calibre 在 EPUB、AZW3 和 PDF 之间转换，MOBI 也可作为转换源；生成的格式会自动归入原书。
- Kindle 书籍可通过右键导出到电脑书库；KFX 会使用随发布包提供的 KFX Input 插件自动转换为 EPUB（不支持绕过 DRM）。
- 显示实时转换进度；任务可缩小到后台，并可从书籍卡片恢复查看。
- 发布包可内置 Calibre 运行时，也可使用环境变量、系统安装目录或 PATH 中的 `ebook-convert`。

### Kindle 传输

- 识别 USB 磁盘以及 WPD/MTP 模式连接的 Kindle，显示设备容量、书籍和封面，并自动记忆设备型号。
- 支持向设备发送书籍、安全删除、传输校验、断线清理和设备插拔监听；支持多选批量导出到电脑书库或从设备删除。
- 仅访问 Kindle 的 `documents` 目录，不修改设备系统数据库。
- Kindle 字体管理可读取、导入、导出和删除设备 `fonts` 目录中的 TTF、OTF 文件。
- Kindle 字典管理可读取、导入、导出和删除设备 `documents\dictionaries` 目录中的 AZW、AZW3、MOBI、KFX 文件。
- 字体和字典操作同时支持 USB 磁盘与 WPD/MTP Kindle，并限制在对应目录内；取消或断连时会清理未完成的传输文件。
- 可读取 Kindle `documents\My Clippings.txt` 中的文字划线与笔记。删除操作仅移除该文件中的记录，不会修改书籍侧车数据库或云端同步标注。
- 支持通过 SMTP 将 EPUB 或 PDF 发送到 Kindle 个人文档邮箱。

### 备份与隐私

- 一键导出或导入 `.kkindle` 备份，迁移书库、封面和阅读记录；可启用每日自动备份与保留数量限制。
- 可在设置中选择默认打开格式、Calibre 路径、AI/网络权限和数据目录，并通过备份包安全迁移数据目录。
- AI API Key 使用 Windows 当前用户加密后保存在本机。
- API Key 和 SMTP 密码不会写入备份包；AI 对话仅发送相关片段，不上传整本书。

## 环境要求

- Windows 11 x64
- 从源码构建需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 格式转换需要 Calibre；可在发布时内置，也可由用户单独安装

## 下载与安装

请从 [GitHub Releases](https://github.com/kingstacker/Kkindle/releases) 下载最新版本：

- `Kkindle-X.Y.Z-win-x64-setup.exe`：推荐的安装版，支持开始菜单快捷方式、可选桌面快捷方式和卸载。
- `Kkindle-X.Y.Z-win-x64-portable.zip`：解压即用的便携版。
- `SHA256SUMS.txt`：安装包与便携包的 SHA-256 校验值。

两种发行包均为 Windows x64 自包含版本，并内置 Calibre 转换运行时。安装版默认安装到当前用户的 `%LOCALAPPDATA%\Programs\Kkindle`，不需要管理员权限；卸载时不会主动删除运行过程中创建的 `data`、`backups` 或 `app-root.json`。

## 从源码运行

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
dotnet run --project src\Kkindle.App\Kkindle.App.csproj -p:Platform=x64
```

## 本地构建发行包

安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 和 Calibre 后运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 `
  -Version 1.0.0 `
  -CalibreRuntime "C:\Program Files\Calibre2"
```

脚本会生成安装版 EXE、便携版 ZIP 和 `SHA256SUMS.txt`。没有 Inno Setup 时可增加 `-SkipInstaller` 仅生成便携版；省略 `-CalibreRuntime` 时不内置 Calibre，程序会依次尝试用户配置的 `KKINDLE_CALIBRE_CONVERT`、系统安装目录和 PATH。

发布结果位于：

```text
artifacts\release\1.0.0\
```

应用数据默认保存在可执行文件旁的 `data` 目录，也可在设置中迁移到其他目录。自动备份位于数据根目录旁的 `backups` 目录。内置 Calibre 的许可证和源代码信息见发布目录中的 `Calibre\LICENSE` 与 `Calibre-THIRD-PARTY-NOTICE.txt`。

## GitHub 自动发版

`.github/workflows/release.yml` 会在推送 `vX.Y.Z` 标签时自动执行 Release 测试、构建自包含应用、内置 Calibre、生成安装版与便携版、计算校验值，并创建 GitHub Release。带后缀的标签（如 `v1.1.0-beta.1`）会发布为预发行版。

```powershell
git tag v1.0.0
git push origin v1.0.0
```

失败后可在 GitHub Actions 中手动运行 Release 工作流并填写已有标签；工作流会覆盖该 Release 中同名产物，不会重复创建版本。

## 验证

项目包含书库与旧数据库迁移、备份、阅读进度、排版、格式策略、PDF 文本提取、词典、字体和应用设置等自动化测试：

```powershell
dotnet test Kkindle.sln -c Debug -p:Platform=x64
dotnet test Kkindle.sln -c Release -p:Platform=x64
```

## 项目结构

```text
src/Kkindle.App             WinUI 3 桌面应用与界面
src/Kkindle.Core            领域模型、策略与服务接口
src/Kkindle.Infrastructure  SQLite、设备、转换、备份与 AI 服务实现
tests/Kkindle.Tests         自动化测试
```

## 参考技术与开源项目

Kkindle 的功能设计与实现使用或参考了以下开源项目。列入本节仅说明技术关系；各项目的源码与组件仍适用其各自的许可证。

### 功能实现参考与外部工具

- [ZlibraryKO/zlibrary.koplugin](https://github.com/ZlibraryKO/zlibrary.koplugin)：Z-Library 登录、搜索、语言与格式筛选、服务地址发现及下载流程的实现参考。
- [kovidgoyal/calibre](https://github.com/kovidgoyal/calibre)：通过独立的 `ebook-convert` 进程完成 EPUB、AZW3、MOBI、PDF 与 KFX 等格式的读取或转换。
- [KFX Input](https://www.mobileread.com/forums/showthread.php?t=291290)：随内置 Calibre 提供的 KFX 输入插件，用于处理无 DRM 的 KFX 文件。

### 应用运行依赖

- [microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK)：WinUI 3 桌面应用框架。
- [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet)：提供 `CommunityToolkit.Mvvm` MVVM 基础设施。
- [dotnet/efcore](https://github.com/dotnet/efcore)：`Microsoft.Data.Sqlite` 的源码项目，用于本地 SQLite 数据存储。
- [UglyToad/PdfPig](https://github.com/UglyToad/PdfPig)：用于读取和提取 PDF 文本。

### 测试工具

- [xunit/xunit](https://github.com/xunit/xunit)：自动化测试框架。
- [xunit/visualstudio.xunit](https://github.com/xunit/visualstudio.xunit)：xUnit 的 Visual Studio 与 .NET 测试适配器。
- [microsoft/vstest](https://github.com/microsoft/vstest)：`Microsoft.NET.Test.Sdk` 对应的测试平台。

## 许可证

本项目基于 [MIT License](LICENSE) 开源。随应用分发的字体、Calibre 等第三方组件适用各自的许可证。

## 致谢

- 社区：[LINUX DO](https://linux.do/?tl=en)
- 公益站：any
