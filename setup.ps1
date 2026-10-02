# setup.ps1 - 一鍵安裝入口（支援 irm | iex）
#
#   執行：
#     irm https://raw.githubusercontent.com/Artin0123/windows-setup/refs/heads/main/setup.ps1 | iex
#
# 流程：
#   1. 下載 csc-build.cmd 與 .cs，編譯成 exe
#   2. 搬到 %USERPROFILE%\portable-exe（launcher 並註冊開機排程）與 %USERPROFILE%\scoop\shims（ffmpeg）
#   3. 依序執行 venv.ps1、windows-optimize.ps1（皆不需管理員）
#   ※ 需要管理員的 windows-optimize-admin.ps1（含 hotkey）不在此流程，請另外執行，結尾會印出指令
#
# 選用環境變數：
#   SETUP_STEPS   只跑部分步驟，逗號分隔：build,task,venv,optimize（預設全部）
#   SETUP_BRANCH  分支名稱（預設 main）

function Invoke-WindowsSetup {
    $ErrorActionPreference = 'Stop'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $branch  = if ($env:SETUP_BRANCH) { $env:SETUP_BRANCH } else { 'main' }
    $baseUrl = "https://raw.githubusercontent.com/Artin0123/windows-setup/refs/heads/$branch"
    $steps   = if ($env:SETUP_STEPS) { $env:SETUP_STEPS -split '[,\s]+' | Where-Object { $_ } } else { @('build', 'task', 'venv', 'optimize') }

    $portableDir = Join-Path $env:USERPROFILE 'portable-exe'
    $shimsDir    = Join-Path $env:USERPROFILE 'scoop\shims'
    $work        = Join-Path $env:TEMP 'windows-setup'
    $buildDir    = Join-Path $work 'build'
    $scriptsDir  = Join-Path $work 'scripts'

    # 每次都重新建立乾淨的暫存資料夾
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $buildDir, $scriptsDir -Force | Out-Null

    $utf8Bom   = New-Object System.Text.UTF8Encoding($true)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    function Get-Remote {
        param([string]$Name, [string]$Out)
        try {
            Invoke-WebRequest -Uri "$baseUrl/$Name" -OutFile $Out -UseBasicParsing
        } catch {
            throw "下載失敗：$Name $($_.Exception.Message)"
        }
    }

    # .ps1：去掉可能的 BOM 後，以「UTF-8 含 BOM」存檔，Windows PowerShell 5.1 才不會把中文當成 ANSI 讀壞
    function Get-RemoteScript {
        param([string]$Name)
        $path = Join-Path $scriptsDir $Name
        Get-Remote $Name $path
        $text = [IO.File]::ReadAllText($path, $utf8NoBom).TrimStart([char]0xFEFF)
        [IO.File]::WriteAllText($path, $text, $utf8Bom)
        return $path
    }

    function Build-Cs {
        param([string]$Cs, [string]$Target, [string[]]$Exes)
        $csPath = Join-Path $buildDir $Cs
        Get-Remote $Cs $csPath            # 原始位元組直接落地（csc 以 /codepage:65001 讀取，不需要 BOM）
        $cscArgs = @('/nopause')
        if ($Target) { $cscArgs += $Target }
        $cscArgs += $csPath
        & (Join-Path $buildDir 'csc-build.cmd') @cscArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "編譯失敗：$Cs" }
        foreach ($exe in $Exes) {
            if (-not (Test-Path (Join-Path $buildDir $exe))) { throw "編譯後找不到：$exe" }
        }
    }

    function Move-Exes {
        param([string[]]$Exes, [string]$DestDir)
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null      # 路徑不存在就自動建立
        foreach ($exe in $Exes) {
            try {
                Move-Item (Join-Path $buildDir $exe) (Join-Path $DestDir $exe) -Force
                Write-Host "  [OK] $DestDir\$exe" -ForegroundColor Green
            } catch {
                Write-Host "  [失敗] $exe 無法覆蓋（程式執行中？）：$($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }

    # ---------------------------------------------------------------- 1 + 2. 編譯並搬移
    if ($steps -contains 'build') {
        Write-Host '=== [1/3] 編譯並搬移 exe ===' -ForegroundColor Cyan

        # csc-build.cmd 必須是「無 BOM + CRLF」：有 BOM 會讓第一行 @echo off 失效；LF 會讓 goto/label 解析異常
        $cmdPath = Join-Path $buildDir 'csc-build.cmd'
        Get-Remote 'csc-build.cmd' $cmdPath
        $cmdText = [IO.File]::ReadAllText($cmdPath, $utf8NoBom).TrimStart([char]0xFEFF) -replace "`r?`n", "`r`n"
        [IO.File]::WriteAllText($cmdPath, $cmdText, $utf8NoBom)

        # launcher 必須用 winexe，否則開機會閃出黑色主控台視窗（csc-build 只會對含 WinForms 的原始碼自動判斷 winexe）
        $targets = @(
            @{ Cs = 'rclone-operator.cs';   Target = '';       Exes = @('rclone-transfer.exe', 'rclone-delete.exe'); Dest = (Join-Path $portableDir 'rclone-operator') },
            @{ Cs = 'move_pi_sessions.cs';  Target = '';       Exes = @('move_pi_sessions.exe');                    Dest = (Join-Path $portableDir 'move-pi-session') },
            @{ Cs = 'launcher.cs';          Target = 'winexe'; Exes = @('launcher.exe');                            Dest = $portableDir },
            @{ Cs = 'ffmpeg.cs';            Target = 'exe';    Exes = @('ffmpeg.exe');                              Dest = $shimsDir }
        )
        foreach ($t in $targets) {
            Write-Host "-> $($t.Cs)" -ForegroundColor Yellow
            Build-Cs -Cs $t.Cs -Target $t.Target -Exes $t.Exes
            Move-Exes -Exes $t.Exes -DestDir $t.Dest
        }
    }

    # ---------------------------------------------------------------- launcher 開機排程
    if ($steps -contains 'task') {
        $launcher = Join-Path $portableDir 'launcher.exe'
        if (Test-Path $launcher) {
            try {
                $user      = "$env:USERDOMAIN\$env:USERNAME"
                $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
                $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
                $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
                $action    = New-ScheduledTaskAction -Execute $launcher
                # 沿用舊名稱，會直接取代 scoop.ps1 時代建立的排程
                Register-ScheduledTask -TaskName 'Scoop-Startup' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
                Write-Host '  [OK] 已註冊開機排程 Scoop-Startup' -ForegroundColor Green
            } catch {
                Write-Host "  [失敗] 註冊排程：$($_.Exception.Message)" -ForegroundColor Red
            }
        } else {
            Write-Host "  [略過] 找不到 $launcher，未註冊排程" -ForegroundColor Yellow
        }
    }

    # 編譯用的暫存檔不再需要
    Remove-Item $buildDir -Recurse -Force -ErrorAction SilentlyContinue

    $psExe = (Get-Process -Id $PID).Path     # 用目前這個 PowerShell 跑子腳本，$PROFILE 才會和你平常用的一致

    # ---------------------------------------------------------------- venv
    if ($steps -contains 'venv') {
        Write-Host '=== [2/3] venv ===' -ForegroundColor Cyan
        $venv = Get-RemoteScript 'venv.ps1'
        & $psExe -NoProfile -ExecutionPolicy Bypass -File $venv
        if ($LASTEXITCODE -ne 0) { Write-Host "  [失敗] venv.ps1 結束碼 $LASTEXITCODE" -ForegroundColor Red }
    }

    # ---------------------------------------------------------------- windows-optimize
    if ($steps -contains 'optimize') {
        Write-Host '=== [3/3] windows-optimize ===' -ForegroundColor Cyan
        $optA = Get-RemoteScript 'windows-optimize.ps1'    # 免管理員（結尾會重啟檔案總管並 Pause）
        & $psExe -NoProfile -ExecutionPolicy Bypass -File $optA
    }

    Write-Host '完成（以上皆未使用管理員權限）。' -ForegroundColor Cyan
    Write-Host '需要管理員的優化與 hotkey 請另外執行：' -ForegroundColor Yellow
    Write-Host "  irm $baseUrl/windows-optimize-admin.ps1 | iex" -ForegroundColor Yellow
}

Invoke-WindowsSetup
