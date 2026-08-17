# 安装 macOS ad-hoc 版本

这个 Kkindle 版本使用 ad-hoc 签名，尚未经过 Apple 公证。虽然应用签名自身有效，
但 macOS 通常仍会阻止从网络下载的应用直接启动。

1. 解压下载的压缩包，把 `Kkindle.app` 移动到 `/Applications`。
2. 按住 Control 点击 `Kkindle.app`，选择“打开”，然后再次确认“打开”。
3. 如果仍被阻止，请打开“系统设置 > 隐私与安全性”，找到 Kkindle 并选择“仍要打开”。

如果以上方法仍不可用，可在终端移除下载隔离标记：

```sh
xattr -dr com.apple.quarantine /Applications/Kkindle.app
open /Applications/Kkindle.app
```

只应对从本项目官方 GitHub Release 页面下载的 Kkindle 使用该命令。后续采用
Developer ID 签名并经过 Apple 公证的版本将不再需要这些手动步骤。
