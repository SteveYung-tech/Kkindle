$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP "install-pwsh.log"
Start-Transcript -Path $log -Force | Out-Null
try {
    $version = '7.4.6'
    $url = "https://github.com/PowerShell/PowerShell/releases/download/v$version/PowerShell-$version-win-x64.msi"
    $out = Join-Path $env:TEMP "PowerShell-$version-win-x64.msi"

    Write-Host "下载 PowerShell $version ..."
    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
    Write-Host "下载完成，开始安装（静默模式）..."
    $msi = Start-Process msiexec.exe -ArgumentList @('/i', "`"$out`"", '/qn', '/norestart') -Wait -PassThru
    Write-Host "msiexec 退出码: $($msi.ExitCode)"
    Write-Host "安装完成。"
}
catch {
    Write-Host "错误: $($_.Exception.Message)"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
