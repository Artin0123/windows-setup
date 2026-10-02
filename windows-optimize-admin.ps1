# Optimize-All.ps1 - 一键优化脚本 (支持 irm | iex 版)

# --- 1. 自动提权逻辑 (修复版) ---
if (-NOT ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "正在请求管理员权限..." -ForegroundColor Yellow
    try {
        if ($PSCommandPath -and (Test-Path $PSCommandPath)) {
            # 模式A: 本地文件执行
            Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`""
        } else {
            # 模式B: irm | iex 执行，没有文件路径
            $tempFile = Join-Path $env:TEMP "Optimize-All.ps1"
            # 把当前内存中的脚本内容写入临时文件
            $MyInvocation.MyCommand.ScriptBlock.ToString() | Set-Content -Path $tempFile -Force -Encoding UTF8
            Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$tempFile`""
        }
    } catch {
        Write-Host "已取消提权，脚本终止。: $($_.Exception.Message)" -ForegroundColor Red
        Read-Host "按 Enter 退出"
    }
    exit
}

Write-Host "已获得管理员权限，开始执行优化..." -ForegroundColor Cyan
Write-Host "----------------------------------------"

# --- 2. ASR 规则 ---
try {
    Add-MpPreference -AttackSurfaceReductionRules_Ids 9e6c4e1f-7d60-472f-ba1a-a39ef669e4b2 -AttackSurfaceReductionRules_Actions AuditMode -ErrorAction Stop
    Write-Host "[1/6] ASR 规则已设为 AuditMode" -ForegroundColor Green
} catch {
    Write-Host "[1/6] ASR 设置失败 (可能未安装 Defender): $($_.Exception.Message)" -ForegroundColor Red
}

# --- 3. 禁用 Ndu 服务 ---
try {
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Ndu" -Name "Start" -Value 4 -Type DWord -Force -ErrorAction Stop
    Write-Host "[2/6] 已禁用 Ndu 服务" -ForegroundColor Green
} catch {
    Write-Host "[2/6] Ndu 设置失败: $($_.Exception.Message)" -ForegroundColor Red
}

# --- 4. 关闭休眠---
try {
    powercfg /h off
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[3/6] 已关闭休眠" -ForegroundColor Green
    } else {
        throw "powercfg 返回错误码 $LASTEXITCODE"
    }
} catch {
    Write-Host "[3/6] 关闭休眠失败: $($_.Exception.Message)" -ForegroundColor Red
}

# --- 5. 解锁 Boost Mode ---
$boostPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7"
if (Test-Path $boostPath) {
    Set-ItemProperty -Path $boostPath -Name "Attributes" -Value 2 -Type DWord -Force
    Write-Host "[4/6] 已解锁：处理器性能提升模式 (Boost Mode)" -ForegroundColor Green
}

# --- 6. 解锁最大处理器频率 ---
$freqPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\75b0ae3f-bce0-45a7-8c89-c9611c25e100"
if (Test-Path $freqPath) {
    Set-ItemProperty -Path $freqPath -Name "Attributes" -Value 2 -Type DWord -Force
    Write-Host "[5/6] 已解锁：最大处理器频率" -ForegroundColor Green
}

# --- 7. 輸入法快捷鍵 (原 hotkey.reg，HKEY_USERS\.DEFAULT 需要管理員) ---
try {
    foreach ($id in '00000010', '00000070') {
        $p = "Registry::HKEY_CURRENT_USER\Control Panel\Input Method\Hot Keys\$id"
        if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
        Set-ItemProperty -Path $p -Name "Key Modifiers" -Value ([byte[]](0x00,0xc0,0x00,0x00)) -Type Binary -Force
        Set-ItemProperty -Path $p -Name "Virtual Key" -Value ([byte[]](0xff,0x00,0x00,0x00)) -Type Binary -Force

        $p = "Registry::HKEY_USERS\.DEFAULT\Control Panel\Input Method\Hot Keys\$id"
        if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
        Set-ItemProperty -Path $p -Name "Key Modifiers" -Value ([byte[]](0x02,0xc0,0x00,0x00)) -Type Binary -Force
        Set-ItemProperty -Path $p -Name "Target IME" -Value ([byte[]](0x00,0x00,0x00,0x00)) -Type Binary -Force
        Set-ItemProperty -Path $p -Name "Virtual Key" -Value ([byte[]](0xff,0x00,0x00,0x00)) -Type Binary -Force
    }
    Write-Host "[6/6] 已套用輸入法快捷鍵 (hotkey)" -ForegroundColor Green
} catch {
    Write-Host "[6/6] hotkey 設定失敗: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "----------------------------------------"
Write-Host "全部完成！" -ForegroundColor Cyan
Read-Host "按 Enter 退出"