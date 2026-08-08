# Kkindle

一个面向 Windows 11 的个人 Kindle 书库管理器，采用 WinUI 3 和灰白纸张风格界面。

## 当前功能

- 独立 SQLite 书库
- 导入 EPUB、PDF、MOBI、AZW3
- EPUB 标题、作者、简介和封面解析
- 文件复制、SHA-256 去重和多格式归档
- 书籍右键支持使用随发布包携带的 Calibre 在 EPUB、AZW3、PDF 之间互转，并将新格式归入原书
- 格式转换显示实时进度，可缩小到后台并在对应书籍右下角保留可恢复的矩形进度块
- Kreader 支持 EPUB、PDF 和 AZW3（AZW3 打开时自动准备为临时 EPUB）
- 搜索、作者/标签/格式筛选、书架视图、列表视图、元数据编辑
- 鼠标拖入书籍、空书库/无结果状态和长标题省略显示
- Kindle USB 磁盘与 WPD/MTP 扫描、封面显示、发送和安全删除
- 设备插拔事件监听，并以三秒轮询作为兼容性兜底
- 中文文件名、断线清理和传输校验

## 开发

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
```

## 发布便携版

```powershell
dotnet publish src\Kkindle.App\Kkindle.App.csproj `
  -c Release -p:Platform=x64 -p:KkindleCalibreRuntime="C:\Program Files\Calibre2" -r win-x64 `
  --self-contained true -p:WindowsAppSDKSelfContained=true
```

`KkindleCalibreRuntime` 会把指定目录下的 Calibre 运行时复制到发布目录的 `Calibre` 子目录；发布后的用户无需另行安装 Calibre。Calibre 的许可证和源代码信息见发布目录中的 `Calibre\LICENSE` 与 `Calibre-THIRD-PARTY-NOTICE.txt`。开发机没有 Calibre 时可省略该参数，程序仍会回退到用户配置的 `KKINDLE_CALIBRE_CONVERT`、系统安装目录或 PATH。

发布目录位于：

```text
src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

应用数据保存在可执行文件旁的 `data` 目录中。连接 Kindle 后，应用只访问设备的 `documents` 目录，不修改 Kindle 系统数据库。
