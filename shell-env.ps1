# shell-env.ps1 - 寫入 Bash (~/.bashrc) 與 PowerShell ($PROFILE) 的基本環境設定
# 以標記區塊管理，可重複執行（已存在就原地更新，不會重複寫入）；
# 也會把舊版「沒有標記」的相同內容認出來並收進區塊，避免重複。

$utf8Bom   = New-Object System.Text.UTF8Encoding($true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-Utf8Text {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "" }
    # 明確以 UTF-8 讀取（5.1 的 Get-Content 會把無 BOM 的 UTF-8 當 ANSI，中文變亂碼）
    return ([System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) -replace "`r`n", "`n" -replace "`r", "`n")
}

# $Text 與 $Inner 皆為 LF 換行。回傳更新後的文字。
function Set-ManagedBlock {
    param(
        [string]$Text,
        [string]$StartMarker,
        [string]$EndMarker,
        [string]$Inner,
        [string[]]$LegacyPatterns = @()    # regex；沒有標記的舊版內容，會被移除並以區塊取代
    )
    $block = $StartMarker + "`n" + $Inner.Trim("`n") + "`n" + $EndMarker

    # 1. 已有標記區塊：第一個原地更新，其餘重複的移除（不用 [regex]::Replace，避免 $_ 之類被展開）
    $pattern = '(?s)' + [regex]::Escape($StartMarker) + '.*?' + [regex]::Escape($EndMarker)
    $found = [regex]::Matches($Text, $pattern)
    if ($found.Count -gt 0) {
        $sb = New-Object System.Text.StringBuilder
        $pos = 0
        $first = $true
        foreach ($m in $found) {
            [void]$sb.Append($Text.Substring($pos, $m.Index - $pos))
            if ($first) { [void]$sb.Append($block); $first = $false }
            $pos = $m.Index + $m.Length
        }
        [void]$sb.Append($Text.Substring($pos))
        return $sb.ToString()
    }

    # 2. 沒有標記：移除舊版相同內容，並把區塊放在第一個舊內容的位置
    $insertAt = -1
    foreach ($pat in $LegacyPatterns) {
        while ($true) {
            $m = [regex]::Match($Text, $pat)
            if (-not $m.Success) { break }
            if ($insertAt -lt 0 -or $m.Index -lt $insertAt) { $insertAt = $m.Index }
            $Text = $Text.Remove($m.Index, $m.Length)
        }
    }
    if ($insertAt -ge 0) {
        return $Text.Substring(0, $insertAt) + $block + "`n" + $Text.Substring($insertAt)
    }

    # 3. 全新：放在檔案最前面
    if ([string]::IsNullOrWhiteSpace($Text)) { return $block + "`n" }
    return $block + "`n`n" + $Text.TrimStart("`n")
}

# ------------------------------------------------------------------------------
# 1. Bash (~/.bashrc)
# ------------------------------------------------------------------------------
$bashrcPath = Join-Path $HOME ".bashrc"
$bashInner = @'
export TERM=xterm-256color
export EDITOR=notepad.exe
# 讓多個終端機視窗的歷史紀錄以「附加」的方式寫入，而不是互相覆蓋
shopt -s histappend
# 忽略重複指令、忽略開頭有空格的指令
export HISTCONTROL=ignoreboth
# 關閉 MSYS 自動路徑轉換：原因與影響見 ./docs/msys-no-pathconv.md
export MSYS_NO_PATHCONV=1
'@ -replace "`r`n", "`n"

# 舊版沒有標記的相同內容（逐行比對，找到就移除並收進區塊）
$bashLegacy = @($bashInner.Trim("`n") -split "`n" | ForEach-Object { '(?m)^' + [regex]::Escape($_) + '[ \t]*(\n|\z)' })
$bashOld = Read-Utf8Text $bashrcPath
$bashNew = Set-ManagedBlock -Text $bashOld -StartMarker '# === SHELL_ENV_START ===' -EndMarker '# === SHELL_ENV_END ===' -Inner $bashInner -LegacyPatterns $bashLegacy
if ($bashNew -ne $bashOld) {
    [System.IO.File]::WriteAllText($bashrcPath, $bashNew, $utf8NoBom)    # bashrc：UTF-8 無 BOM + LF
    Write-Host "[Bash] 已更新 $bashrcPath 的環境變數區塊。" -ForegroundColor Green
} else {
    Write-Host "[Bash] 環境變數區塊已是最新，跳過。" -ForegroundColor Yellow
}


# ------------------------------------------------------------------------------
# 2. PowerShell ($PROFILE)
# ------------------------------------------------------------------------------
$psInner = @'
# 效能原則：此檔每個新 pwsh 行程都會執行，熱路徑一律用 .NET BCL（[IO.*]）而非 cmdlet——
# Join-Path/Test-Path 在全新行程會觸發模組自動載入掃描（實測 +150ms），[IO.*] 只要 +35ms。
# ⚠️ 刻意不用 scoop 的 current junction（scoop\apps\git\current\bin），改用版本目錄掃描——這是設計決策，勿改回：
# junction 路徑會被外部程式快取或寫入登錄（系統匣圖示、捷徑解析等），scoop 更新換版後
# 舊的快取/登錄資訊會指錯而失效，實例見 v2rayN #5759：
# https://github.com/2dust/v2rayN/issues/5759#issuecomment-3476104944
# 多花幾 ms 換取 PATH 永遠指向真實版本目錄，值得。以下以純 .NET 實作掃描（不用 cmdlet，
# 避免每個新 pwsh 行程觸發模組自動載入掃描，實測 cmdlet 版 +150ms）。
$GitBashBin = $null
$gitApps = [IO.Path]::Combine($HOME, 'scoop\apps\git')
if ([IO.Directory]::Exists($gitApps)) {
    $bestName = $null; $bestVer = $null
    foreach ($d in [IO.Directory]::EnumerateDirectories($gitApps)) {
        $n = [IO.Path]::GetFileName($d)
        if ($n -notmatch '^\d+(\.\d+)+') { continue }
        if (-not [IO.File]::Exists([IO.Path]::Combine($d, 'bin\bash.exe'))) { continue }
        $v = [version]($n -replace '-.*$', '')
        if ($null -eq $bestVer -or $v -gt $bestVer) { $bestName = $n; $bestVer = $v }
    }
    if ($bestName) { $GitBashBin = [IO.Path]::Combine($gitApps, $bestName, 'bin') }
}

if ($GitBashBin -and (($env:Path -split ';') -notcontains $GitBashBin)) {
    $env:Path = "$GitBashBin;$env:Path"
}

# 設定 PowerShell 預設讀寫檔案的編碼為 UTF-8
$PSDefaultParameterValues['*:Encoding'] = 'utf8'

# 設定 Console 輸出與輸入的編碼為 UTF-8
# （pwsh 7 啟動時已是 UTF-8——實測 [Console]::Input/OutputEncoding 均為 utf-8，此行只對 PS5 有意義）
if ($PSVersionTable.PSVersion.Major -le 5) {
    [console]::InputEncoding = [console]::OutputEncoding = [System.Text.Encoding]::UTF8
}

# PS5 專屬：升級熱鍵——互動 prompt 按 Alt+` → 清屏並啟動巢狀 pwsh（含完整環境）。
# 巢狀而非轉導：自動化呼叫（-Command/-File/-EncodedCommand）保留真 5.1；巢狀 pwsh 裡再按 = 無操作。
if ($PSVersionTable.PSVersion.Major -le 5) {
    $cl = [Environment]::GetCommandLineArgs()
    if (-not ($cl -match '^[/-](command|c|file|f|encodedcommand|e|noni|noninteractive)')) {
        try {
            # 可用 chord：Alt+`、Shift+F2；不可用（實測不派送或有副作用）：Ctrl+Alt 系、Shift+可印字元、Ctrl+字母、Alt+Q
            Set-PSReadLineKeyHandler -Chord 'Alt+`' -ScriptBlock {
                # clear-host 在接受指令後才執行，會把「輸入的 pwsh 那行」和 PS5 banner 一起擦掉，
                # 畫面只剩 pwsh 的 banner + prompt，看起來像直接開了一個全新的 pwsh
                [Microsoft.PowerShell.PSConsoleReadLine]::Insert('clear-host; pwsh')
                [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
            }
        } catch { }
    }
}
'@ -replace "`r`n", "`n"

# 正典 pwsh profile（Documents\PowerShell）與 PS5 fallback（Documents\WindowsPowerShell）
# 都寫入同一份 SHELL_ENV；venv／prompt／轉導 shim 由 venv.ps1 管理
$docDir = [Environment]::GetFolderPath('MyDocuments')
$psTargets = @(
    (Join-Path $docDir 'PowerShell\Microsoft.PowerShell_profile.ps1'),
    (Join-Path $docDir 'WindowsPowerShell\Microsoft.PowerShell_profile.ps1')
)
foreach ($psProfilePath in $psTargets) {
    $psDir = Split-Path $psProfilePath
    if (-not (Test-Path $psDir)) { New-Item -ItemType Directory -Force -Path $psDir | Out-Null }

    $psLegacy = @('(?m)' + [regex]::Escape($psInner.Trim("`n")) + '[ \t]*(\n|\z)')
    $psOld = Read-Utf8Text $psProfilePath
    $psNew = Set-ManagedBlock -Text $psOld -StartMarker '# === SHELL_ENV_START ===' -EndMarker '# === SHELL_ENV_END ===' -Inner $psInner -LegacyPatterns $psLegacy
    if ($psNew -ne $psOld) {
        # $PROFILE：UTF-8 含 BOM + CRLF
        [System.IO.File]::WriteAllText($psProfilePath, ($psNew -replace "`n", "`r`n"), $utf8Bom)
        Write-Host "[PowerShell] 已更新 $psProfilePath 的環境設定區塊。" -ForegroundColor Green
    } else {
        Write-Host "[PowerShell] 環境設定區塊已是最新，跳過：$psProfilePath" -ForegroundColor Yellow
    }
}

# FIRECRAWL_API_KEY 空值占位：只放 pwsh 的 $PROFILE；
# 門檻是「檔案裡有沒有賦值行」——使用者填過值之後，此段永遠不會再動它
foreach ($psProfilePath in ($psTargets | Where-Object { $_ -notmatch 'WindowsPowerShell' })) {
    $psCur = Read-Utf8Text $psProfilePath
    if ($psCur -notmatch '(?m)^\s*\$env:FIRECRAWL_API_KEY\s*=') {
        $apiBlock = '# === API_KEY_PLACEHOLDER_START ===' + "`n" + '$env:FIRECRAWL_API_KEY = ''''' + "`n" + '# === API_KEY_PLACEHOLDER_END ==='
        [System.IO.File]::WriteAllText($psProfilePath, ($psCur.TrimEnd("`n") + "`n`n" + ($apiBlock -replace "`n", "`r`n") + "`n"), $utf8Bom)
        Write-Host '[PowerShell] 已補上 FIRECRAWL_API_KEY 空值占位（請自行填入 key）。' -ForegroundColor Yellow
    }
}