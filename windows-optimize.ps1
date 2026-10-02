# Win10 优化 - A脚本 (免管理员权限)
# 包含：1.禁用Bing搜索 2.禁用文件夹自动发现
# 效果：开始菜单秒开 + 大文件夹不卡绿条

Write-Host "正在执行 A脚本 (无需管理员)..." -ForegroundColor Cyan

$searchPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search"
if (-not (Test-Path $searchPath)) { New-Item -Path $searchPath -Force | Out-Null }
Set-ItemProperty -Path $searchPath -Name "BingSearchEnabled" -Value 0 -Type DWord -Force
Write-Host "[1/2] 已禁用 Bing 在线搜索" -ForegroundColor Green

$folderPath = "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell"
if (-not (Test-Path $folderPath)) { New-Item -Path $folderPath -Force | Out-Null }
Set-ItemProperty -Path $folderPath -Name "FolderType" -Value "NotSpecified" -Type String -Force
Write-Host "[2/2] 已禁用文件夹自动发现" -ForegroundColor Green

Write-Host "完成。设置已写入，重新登录或新开窗口后完全生效。" -ForegroundColor Yellow
Pause