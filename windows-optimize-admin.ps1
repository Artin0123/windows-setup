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
    Write-Host "[1/8] ASR 规则已设为 AuditMode" -ForegroundColor Green
} catch {
    Write-Host "[1/8] ASR 设置失败 (可能未安装 Defender): $($_.Exception.Message)" -ForegroundColor Red
}

# --- Defender 排除清單 (pwsh 啟動加速；可重複執行，不累積重複項) ---
try {
    $mp = Get-MpPreference -ErrorAction Stop
    $procs = @('pwsh.exe', 'powershell.exe') | Where-Object { $mp.ExclusionProcess -notcontains $_ }
    $paths = @("$env:USERPROFILE\scoop", "$env:USERPROFILE\Documents\PowerShell", "$env:USERPROFILE\Documents\WindowsPowerShell") | Where-Object { $mp.ExclusionPath -notcontains $_ }
    if ($procs -or $paths) {
        if ($procs) { Add-MpPreference -ExclusionProcess $procs -ErrorAction Stop }
        if ($paths) { Add-MpPreference -ExclusionPath $paths -ErrorAction Stop }
        Write-Host "[2/8] Defender 排除清單已更新 (process: $($procs -join ', ')；path: $($paths -join ', '))" -ForegroundColor Green
    } else {
        Write-Host "[2/8] Defender 排除清單已是最新，跳過。" -ForegroundColor Yellow
    }
} catch {
    Write-Host "[2/8] Defender 排除設定失敗 (可能未安裝 Defender 或權限不足): $($_.Exception.Message)" -ForegroundColor Red
}

# --- 3. 禁用 Ndu 服务 ---
try {
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Ndu" -Name "Start" -Value 4 -Type DWord -Force -ErrorAction Stop
    Write-Host "[3/8] 已禁用 Ndu 服务" -ForegroundColor Green
} catch {
    Write-Host "[3/8] Ndu 设置失败: $($_.Exception.Message)" -ForegroundColor Red
}

# --- 4. 关闭休眠---
try {
    powercfg /h off
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[4/8] 已关闭休眠" -ForegroundColor Green
    } else {
        throw "powercfg 返回错误码 $LASTEXITCODE"
    }
} catch {
    Write-Host "[4/8] 关闭休眠失败: $($_.Exception.Message)" -ForegroundColor Red
}

# --- 5. 解锁 Boost Mode ---
$boostPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7"
if (Test-Path $boostPath) {
    Set-ItemProperty -Path $boostPath -Name "Attributes" -Value 2 -Type DWord -Force
    Write-Host "[5/8] 已解锁：处理器性能提升模式 (Boost Mode)" -ForegroundColor Green
}

# --- 6. 解锁最大处理器频率 ---
$freqPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\75b0ae3f-bce0-45a7-8c89-c9611c25e100"
if (Test-Path $freqPath) {
    Set-ItemProperty -Path $freqPath -Name "Attributes" -Value 2 -Type DWord -Force
    Write-Host "[6/8] 已解锁：最大处理器频率" -ForegroundColor Green
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
    Write-Host "[7/8] 已套用輸入法快捷鍵 (hotkey)" -ForegroundColor Green
} catch {
    Write-Host "[7/8] hotkey 設定失敗: $($_.Exception.Message)" -ForegroundColor Red
}

# --- 8. Microsoft Edge 策略 (Policies) ---
try {
    $edgePolicyPath = "HKLM:\SOFTWARE\Policies\Microsoft\Edge"
    if (-not (Test-Path $edgePolicyPath)) {
        New-Item -Path $edgePolicyPath -Force -ErrorAction Stop | Out-Null
    }
    $edgePolicies = [ordered]@{
        HideFirstRunExperience                        = 1
        DefaultBrowserSettingsCampaignEnabled         = 0
        DefaultBrowserSettingEnabled                  = 0
        DnsOverHttpsMode                              = 'automatic'
        DnsOverHttpsTemplates                         = 'https://8lowgtazs2.cloudflare-gateway.com/dns-query{?dns}'
        PersonalizationReportingEnabled               = 0
        SpotlightExperiencesAndRecommendationsEnabled = 0
        UserFeedbackAllowed                           = 0
        DiagnosticData                                = 0
        GenAILocalFoundationalModelSettings           = 1
        SearchbarAllowed                              = 0
        StandaloneHubsSidebarEnabled                  = 0
    }
    foreach ($entry in $edgePolicies.GetEnumerator()) {
        $type = if ($entry.Value -is [int]) { 'DWord' } else { 'String' }
        Set-ItemProperty -Path $edgePolicyPath -Name $entry.Key -Value $entry.Value -Type $type -Force -ErrorAction Stop
    }
    Write-Host "[8/8] 已套用 Microsoft Edge 策略 ($($edgePolicies.Count) 項)" -ForegroundColor Green
} catch {
    Write-Host "[8/8] Microsoft Edge 策略設定失敗: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "----------------------------------------"
Write-Host "全部完成！" -ForegroundColor Cyan
Read-Host "按 Enter 退出"