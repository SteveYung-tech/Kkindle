# Kkindle

一个面向 Windows 11 的个人 Kindle 书库管理器，采用 WinUI 3 和灰白纸张风格界面。

## 当前功能

- 独立 SQLite 书库
- 导入 EPUB、PDF、MOBI、AZW3
- EPUB 标题、作者、简介和封面解析
- 文件复制、SHA-256 去重和多格式归档
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
  -c Release -p:Platform=x64 -r win-x64 `
  --self-contained true -p:WindowsAppSDKSelfContained=true
```

发布目录位于：

```text
src\Kkindle.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

应用数据保存在可执行文件旁的 `data` 目录中。连接 Kindle 后，应用只访问设备的 `documents` 目录，不修改 Kindle 系统数据库。
