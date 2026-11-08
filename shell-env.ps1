# shell-env.ps1 - 寫入 Bash (~/.bashrc) 與 PowerShell ($PROFILE) 的環境設定（含 venv 自動啟動）
# 以標記區塊管理，可重複執行（已存在就原地更新，不會重複寫入）。
# 前提：標記區塊只由本腳本維護、不手動編輯；唯一要人填的是 FIRECRAWL_API_KEY。
#
# ※※※ 本檔必須以 UTF-8 with BOM 儲存 ※※※
# PS 5.1 對無 BOM 的 .ps1 以 ANSI 解析，中文註解亂碼會導致解析直接失敗。

# CurrentUserCurrentHost profile：WindowsPowerShell (PS5) 一律設定；pwsh (PowerShell 7+) 則在偵測到環境中有安裝時才加入
$docDir = [Environment]::GetFolderPath('MyDocuments')
$hasPwsh = ($PSVersionTable.PSEdition -eq 'Core') -or
    [bool](Get-Command pwsh -ErrorAction SilentlyContinue) -or
    ($env:ProgramFiles -and [IO.File]::Exists([IO.Path]::Combine($env:ProgramFiles, 'PowerShell\7\pwsh.exe')))

$psTargets = @(
    if ($hasPwsh) { (Join-Path $docDir 'PowerShell\Microsoft.PowerShell_profile.ps1') }
    (Join-Path $docDir 'WindowsPowerShell\Microsoft.PowerShell_profile.ps1')
)

# 無頭判定：profile 的 SHELL_ENV 區塊（檔頭）與 NATIVE_VENV 區塊（檔尾）各內嵌一份，
# 在區塊字串裡放一行 __HEADLESS_SNIPPET__ 再 .Replace 即可。產出變數 $__isHeadless。
$HeadlessSnippet = (@'
# === 無頭判定 ===
# 輸出被重導，或以 -NonInteractive / -EncodedCommand / -File / -Command 啟動者視為無頭（不畫 prompt、不註冊熱鍵）。
# 例外：帶 -NoExit 者一定是互動——VSCode/Cursor 終端就是以「-NoExit -Command 注入 shell integration」啟動，
# 以此判斷，不必依賴 TERM_PROGRAM 這類宿主專屬的環境變數。
$__cl = [Environment]::GetCommandLineArgs()
$__isHeadless = [Console]::IsOutputRedirected -or [bool]($__cl -match '^-(NonI|NonInteractive)$') -or
    (-not [bool]($__cl -match '^-noe') -and [bool]($__cl -match '^-(c|command|e|ec|encodedcommand|f|file)$'))
'@ -replace "`r`n", "`n").Trim("`n")

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
        [switch]$Append                    # 新區塊放檔尾而非檔頭（venv 的 prompt 包裝必須在其他設定之後）
    )
    # 設計前提：這些檔案的標記區塊一律由本腳本自動維護、不手動編輯，
    # 所以不處理「沒有標記的舊內容」或「標記被改壞」的情況。
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

    # 2. 全新：預設放在檔案最前面；-Append 放檔尾
    if ([string]::IsNullOrWhiteSpace($Text)) { return $block + "`n" }
    if ($Append) { return $Text.TrimEnd("`n") + "`n`n" + $block + "`n" }
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
# bash -c 時 TMPDIR 是空的，"$TMPDIR/x" 會悄悄變成 /x（解析到 git 安裝目錄）。
# 用 LOCALAPPDATA 而非 TEMP：後者在 bash 裡已是 /tmp，=1 時原生程式不會轉換。
if [ -n "$LOCALAPPDATA" ]; then export TMPDIR="${LOCALAPPDATA//\\//}/Temp"; fi
# cmd 開關修復（argv 層）：以 MSYS_NO_PATHCONV 是否存在決定方向，=0 與 =1 都兼容，與 MSYS 判斷方式一致；
# =1 把 //c 削成 /c；=0 模式把 /c 補成 //c 讓轉換層削回 /c。換回 =0 與裝函式是兩個獨立決定。
# sw 旗標只處理第一個 /c、/k 之前的開關區，命令字串本身不碰（cmd /c "echo //c stays" 原樣輸出）。
# [cCkK]* 只比開頭：cmd 接受附著寫法（cmd /cver 能動）。
# 涵蓋不到的尾巴：env cmd、timeout 5 cmd、command cmd、完整路徑、$COMSPEC——漏掉只是維持原狀。
_cmdsw() { local a=() sw=1 x
  for x in "$@"; do
    if [ $sw = 1 ]; then
      if [ -n "${MSYS_NO_PATHCONV+x}" ]; then case "$x" in //[A-Za-z]*) x="${x#/}";; esac
      else case "$x" in /[A-Za-z]*) x="/$x";; esac; fi
      case "${x#/}" in [cCkK]*) sw=0;; esac
    fi
    a+=("$x")
  done; command cmd "${a[@]}"; }
cmd() { _cmdsw "$@"; }; cmd.exe() { _cmdsw "$@"; }
# 具名工具的單斜線旗標修復（taskkill /IM 等）：只動「單一段、開頭是字母或 ?」的 token；
# 含第二個斜線的 token 不動，所以 //server/share 這類 UNC 與 /tmp/x 這類真路徑不會被碰。
# 列已知工具而非通用規則：bash 沒有執行前通用鉤子，DEBUG trap 屬輸入端 lexer（開放集合），不採用。
_sf() { local t="$1"; shift; local a=() x
  for x in "$@"; do
    if [ -n "${MSYS_NO_PATHCONV+x}" ]; then case "$x" in //[A-Za-z?]*) [[ "${x#//}" != */* ]] && x="${x#/}";; esac
    else case "$x" in /[A-Za-z?]*) [[ "${x#/}" != */* ]] && x="/$x";; esac; fi
    a+=("$x")
  done; command "$t" "${a[@]}"; }
taskkill() { _sf taskkill "$@"; }; tasklist() { _sf tasklist "$@"; }; findstr() { _sf findstr "$@"; }
'@ -replace "`r`n", "`n"

$bashOld = Read-Utf8Text $bashrcPath
$bashNew = Set-ManagedBlock -Text $bashOld -StartMarker '# === SHELL_ENV_START ===' -EndMarker '# === SHELL_ENV_END ===' -Inner $bashInner
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
        # 只取開頭的數字版本：scoop 目錄名可能帶 .windows.1 之類後綴，直接轉 [version] 會丟例外，讓每個新視窗都報錯
        $v = [version][regex]::Match($n, '^\d+(\.\d+){1,3}').Value
        if ($null -eq $bestVer -or $v -gt $bestVer) { $bestName = $n; $bestVer = $v }
    }
    if ($bestName) { $GitBashBin = [IO.Path]::Combine($gitApps, $bestName, 'bin') }
}

if ($GitBashBin -and (($env:Path -split ';') -notcontains $GitBashBin)) {
    $env:Path = "$GitBashBin;$env:Path"
}

# Console 輸入/輸出編碼（native 程式的 stdin 解碼／stdout 解碼，兩個方向各管一邊，缺一不可——已實測）。
# ⚠️ PS5 與 pwsh 7 都要設，不能用版本判斷排除 pwsh：pwsh 7 只有在「繼承了父行程的 65001 console」時才是 UTF-8；
# 無頭 spawn（工具/IDE 在沒有 console 的情況下起行程，會新建隱藏 console，code page 回落系統 OEM，簡中是 936）
# 實測 [Console]::OutputEncoding 是 936，native 程式的輸出會被當 GBK 解碼而亂碼。互動視窗測到 65001 不代表無頭也是。
[console]::InputEncoding = [console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 以下兩項只有 PS5 需要；pwsh 7 的預設已是 UTF-8 無 BOM（實測），設了反而可能多出 BOM 或等於沒作用。
if ($PSVersionTable.PSVersion.Major -le 5) {
    # cmdlet 寫檔（Out-File/Set-Content…）的預設編碼：PS5 預設是系統 ANSI（GBK），改成 UTF-8（含 BOM）
    $PSDefaultParameterValues['*:Encoding'] = 'utf8'
    # PowerShell 餵給 native 程式 stdin 的編碼：PS5 預設 ASCII，中文會變成 ?；
    # 必須用「無 BOM」的 UTF-8——直接用 [Text.Encoding]::UTF8 會在 stdin 開頭多送一個 BOM（實測）
    $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}

# PS5 專屬：升級熱鍵——互動 prompt 按 Alt+` → 清屏並啟動巢狀 pwsh（含完整環境）。
# 做法：不在 handler 內直接起行程，而是把 "Clear-Host; pwsh" 當成使用者輸入並送出（AcceptLine），
# 這樣 venv 提示字元 (.venv) PS ...> 下也一樣能用，pwsh 繼承目前目錄與 PATH。
# 巢狀而非轉導：無頭的自動化呼叫（-File/-EncodedCommand/-NonInteractive/輸出被重導）保留真 5.1、不註冊熱鍵；
# 只在 PS5 註冊，所以巢狀 pwsh 裡按 = 無操作。
__HEADLESS_SNIPPET__
if ($PSVersionTable.PSVersion.Major -le 5 -and -not $__isHeadless) {
    try {
        Set-PSReadLineKeyHandler -Chord 'Alt+`' -BriefDescription 'UpgradeToPwsh' `
            -Description 'Clear screen and start pwsh' -ScriptBlock {
            [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine()
            [Microsoft.PowerShell.PSConsoleReadLine]::Insert('Clear-Host; pwsh')
            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
        }
    } catch { }
}
'@ -replace "`r`n", "`n"

# 兩個 profile 內容相同（版本差異用內部 gate 處理）
$psInner = $psInner.Replace('__HEADLESS_SNIPPET__', $HeadlessSnippet)
foreach ($psProfilePath in $psTargets) {
    $psDir = Split-Path $psProfilePath
    if (-not (Test-Path $psDir)) { New-Item -ItemType Directory -Force -Path $psDir | Out-Null }

    $psOld = Read-Utf8Text $psProfilePath
    $psNew = Set-ManagedBlock -Text $psOld -StartMarker '# === SHELL_ENV_START ===' -EndMarker '# === SHELL_ENV_END ===' -Inner $psInner
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

# ------------------------------------------------------------------------------
# 3. venv 自動啟動：PowerShell 引擎（寫入兩個 profile 的檔尾）
# ------------------------------------------------------------------------------
# 區塊必須放檔尾：prompt 函式要包住前面所有設定留下的 prompt。
# PS5 不做任何轉導：期待 5.1 的呼叫端得到真 5.1 語意；互動的「升級到 7」由上面的 Alt+` 熱鍵處理（巢狀，非轉導）。
$venvPsInner = @'
__HEADLESS_SNIPPET__

# 1. 歷史紀錄最佳化（僅互動；Set-PSReadLineOption 會隱式載入 PSReadLine，實測無頭行程要多付 ~150ms）
if (-not $__isHeadless) {
    Set-PSReadLineOption -HistoryNoDuplicates
    Set-PSReadLineOption -AddToHistoryHandler {
        param([string]$line)
        if ([string]::IsNullOrWhiteSpace($line)) { return $false }
        if ($line.StartsWith(" ")) { return $false }
        return $true
    }
}

# 2. 原生極速虛擬環境切換引擎（純 .NET，零 cmdlet）
# 回傳 $true 表示目前 PATH 確實指向指定 venv（避免只有 VIRTUAL_ENV、PATH 未切的半啟動）。
# 刻意只認 PATH 第一段：排在更前面的 python 才會真正接管命令列。
# 已知取捨：其他工具 hook 暫時把別的目錄插到最前時，下一個 prompt 會「拔舊掛新」
# 把 venv bin 推回最前（接受此行為以維持 python 解析正確）。
function global:_Venv-Is-Live {
    param([string]$venvPath, [string]$binDir)
    if ([string]::IsNullOrEmpty($venvPath)) { return $false }
    if ($env:VIRTUAL_ENV -ne $venvPath) { return $false }
    $newBin = [IO.Path]::Combine($venvPath, $binDir)
    $sep = [IO.Path]::PathSeparator
    $first = ($env:PATH -split $sep)[0]
    if ($first -eq $newBin) { return $true }
    try {
        return ([IO.Path]::GetFullPath($first) -eq [IO.Path]::GetFullPath($newBin))
    } catch {
        return $false
    }
}

# 函式刻意掛 global；以 _Venv- / Auto- 前綴避開與使用者自訂函式衝突
function global:Auto-Activate-Venv {
    $current = $PWD.Path
    $venvPath = $null

    # 本 profile 僅部署於 Windows；POSIX（bin/python）分支已移除——bash 版引擎在 .bashrc
    $binDir = "Scripts"
    $exeName = "python.exe"
    $sep = [IO.Path]::PathSeparator

    # 快速路徑：同一 venv 仍為真啟用狀態，且 cwd 仍在該 venv 資料夾內（如 .venv\Scripts）-> 直接返回，零磁碟存取。
    # 在專案根目錄時不會命中，會走下面的向上尋找（實測每個 prompt 約 0.3ms；
    # 把快速路徑放寬到專案根只能降到約 0.13ms，且巢狀 venv 會判錯，不值得）。
    if (_Venv-Is-Live -venvPath $env:VIRTUAL_ENV -binDir $binDir) {
        if ($PWD.Path -eq $env:VIRTUAL_ENV -or
            $PWD.Path.StartsWith("$env:VIRTUAL_ENV$sep", [StringComparison]::OrdinalIgnoreCase)) { return }
    }

    # 向上遞迴尋找虛擬環境（.venv 優先於 venv；正斜線在 Windows/Linux 皆可用）
    while (![string]::IsNullOrEmpty($current)) {
        if ([IO.File]::Exists([IO.Path]::Combine($current, ".venv/$binDir/$exeName"))) {
            $venvPath = [IO.Path]::Combine($current, '.venv'); break
        }
        if ([IO.File]::Exists([IO.Path]::Combine($current, "venv/$binDir/$exeName"))) {
            $venvPath = [IO.Path]::Combine($current, 'venv'); break
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($null -eq $parent -or $parent -eq $current) { break }
        $current = $parent
    }

    # 已真正啟用同一環境 -> 跳過
    if (_Venv-Is-Live -venvPath $venvPath -binDir $binDir) { return }

    # 退出舊環境：從 PATH 中拔除舊的 bin/Scripts 路徑（保留空項以維持原 PATH 結構）
    if ($env:VIRTUAL_ENV) {
        $oldBin = [IO.Path]::Combine($env:VIRTUAL_ENV, $binDir)
        $paths = foreach ($p in ($env:PATH -split $sep)) {
            if ($p.TrimEnd('\') -ine $oldBin.TrimEnd('\')) { $p }
        }
        $env:PATH = $paths -join $sep
        $env:VIRTUAL_ENV = $null
    }

    # 進入新環境：把新的 bin/Scripts 路徑塞入 PATH 最前方
    if ($venvPath) {
        $env:VIRTUAL_ENV = $venvPath
        $newBin = [IO.Path]::Combine($venvPath, $binDir)
        $env:PATH = "$newBin$sep$env:PATH"
    }
}

# 3. Prompt（僅互動用；函式定義本身零成本，無頭行程只多付解析）
if (-not $global:_venv_original_prompt) {
    if ($function:prompt) { $global:_venv_original_prompt = $function:prompt }
    else { $global:_venv_original_prompt = { "PS $($executionContext.SessionState.Path.CurrentLocation)> " } }
}

function global:prompt {
    Auto-Activate-Venv

    if ($env:VIRTUAL_ENV) {
        Write-Host -NoNewline -ForegroundColor Green "($([IO.Path]::GetFileName($env:VIRTUAL_ENV))) "
    }

    & $global:_venv_original_prompt
}

# Profile 載入時直接執行一次（非互動式 / Agent 終端不會畫 prompt）
Auto-Activate-Venv
'@ -replace "`r`n", "`n"
$venvPsInner = $venvPsInner.Replace('__HEADLESS_SNIPPET__', $HeadlessSnippet)

foreach ($psProfilePath in $psTargets) {
    $psOld = Read-Utf8Text $psProfilePath
    $psNew = Set-ManagedBlock -Append -Text $psOld -StartMarker '# === NATIVE_VENV_AUTO_ACTIVATE_START ===' -EndMarker '# === NATIVE_VENV_AUTO_ACTIVATE_END ===' -Inner $venvPsInner
    if ($psNew -ne $psOld) {
        [System.IO.File]::WriteAllText($psProfilePath, ($psNew -replace "`n", "`r`n"), $utf8Bom)
        Write-Host "[PowerShell] 已更新 $psProfilePath 的 venv 自動啟動區塊。" -ForegroundColor Green
    } else {
        Write-Host "[PowerShell] venv 區塊已是最新，跳過：$psProfilePath" -ForegroundColor Yellow
    }
}

# ------------------------------------------------------------------------------
# 4. venv 自動啟動：Bash 引擎（~/.bashrc 檔尾）
# ------------------------------------------------------------------------------
$venvBashInner = @'
# 自動偵測並啟動/退出虛擬環境
# 回傳 0 表示目前 python 確實來自指定 venv（避免只有 VIRTUAL_ENV、PATH 未切的半啟動）
_venv_is_live() {
    local venv="$1"
    local py
    [ -n "$venv" ] || return 1
    [ "$VIRTUAL_ENV" = "$venv" ] || return 1
    py="$(command -v python 2>/dev/null || command -v python3 2>/dev/null)" || return 1
    case "$py" in
        "$venv"/*) return 0 ;;
        *) return 1 ;;
    esac
}
auto_activate_venv() {
    local target_dir="$PWD"
    local venv_path=""
    local activate_script=""
    # 向上遞迴尋找虛擬環境 (.venv 或 venv)
    # 防護：POSIX 走訪會終止於 "/"，但 Windows 磁碟根（C:/ → C: → .）dirname 不再縮短，
    # 缺防護會變成無限迴圈（PWD 為 Windows 形式時觸發，bash 啟動即卡死）
    while [ -n "$target_dir" ] && [ "$target_dir" != "/" ] && [ "$target_dir" != "." ]; do
        if [ -f "$target_dir/.venv/Scripts/activate" ]; then
            venv_path="$target_dir/.venv"
            activate_script="$target_dir/.venv/Scripts/activate"
            break
        elif [ -f "$target_dir/.venv/bin/activate" ]; then
            venv_path="$target_dir/.venv"
            activate_script="$target_dir/.venv/bin/activate"
            break
        elif [ -f "$target_dir/venv/Scripts/activate" ]; then
            venv_path="$target_dir/venv"
            activate_script="$target_dir/venv/Scripts/activate"
            break
        elif [ -f "$target_dir/venv/bin/activate" ]; then
            venv_path="$target_dir/venv"
            activate_script="$target_dir/venv/bin/activate"
            break
        fi
        local prev="$target_dir"
        target_dir="${target_dir%/*}"
        # forkless dirname：${var%/*} 純參數展開，省去每層一次 fork（MSYS fork 實測 ~20ms/次）；不再縮短即到底，防止無限迴圈
        [ "$target_dir" = "$prev" ] && break
    done
    # 情況 A：離開專案或切換到其他環境 -> 自動 deactivate
    if [ -n "$VIRTUAL_ENV" ] && [ "$VIRTUAL_ENV" != "$venv_path" ]; then
        if declare -f deactivate >/dev/null; then
            deactivate
        else
            unset VIRTUAL_ENV
        fi
    fi
    # 情況 B：發現 venv，且尚未真正啟用（含 VIRTUAL_ENV 有設但 PATH 未切）-> source activate
    if [ -n "$venv_path" ] && ! _venv_is_live "$venv_path"; then
        # 半啟動時 deactivate 可能不存在，先清掉以免 activate 誤判
        if [ -n "$VIRTUAL_ENV" ] && ! declare -f deactivate >/dev/null; then
            unset VIRTUAL_ENV
        fi
        source "$activate_script"
    fi
}
# Prompt 鉤子與 History 即時同步（每次 prompt 都檢查，修復 PATH 被重置的半啟動）
_venv_prompt_command() {
    history -a
    history -n
    auto_activate_venv
}
# 註冊至 PROMPT_COMMAND（bash 5.1+ 的 PROMPT_COMMAND 可為陣列；此處僅偵測字串形式，
# 陣列環境最壞情況是重複註冊，行為仍正確、僅每個 prompt 多跑一次）
if [[ ";;$PROMPT_COMMAND;" != *";_venv_prompt_command;"* ]]; then
    PROMPT_COMMAND="_venv_prompt_command${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
fi
# 非互動式 shell（如 AI agent 的 bash 工具）不會跑 PROMPT_COMMAND，
# 且可能透過 BASH_ENV 直接載入本檔，故在載入時主動觸發一次 venv 自動啟動
auto_activate_venv
'@ -replace "`r`n", "`n"

$bashOld = Read-Utf8Text $bashrcPath
$bashNew = Set-ManagedBlock -Append -Text $bashOld -StartMarker '# === BASH_VENV_AUTO_ACTIVATE_START ===' -EndMarker '# === BASH_VENV_AUTO_ACTIVATE_END ===' -Inner $venvBashInner
if ($bashNew -ne $bashOld) {
    [System.IO.File]::WriteAllText($bashrcPath, $bashNew, $utf8NoBom)    # bashrc：UTF-8 無 BOM + LF
    Write-Host "[Bash] 已更新 $bashrcPath 的 venv 自動啟動區塊。" -ForegroundColor Green
} else {
    Write-Host "[Bash] venv 區塊已是最新，跳過。" -ForegroundColor Yellow
}

# ------------------------------------------------------------------------------
# 5. 確保 login shell 讀得到 ~/.bashrc
#     bash 的 login shell 只讀 ~/.bash_profile / ~/.bash_login / ~/.profile 三者的第一個，
#     不會直接讀 ~/.bashrc（Git Bash 僅在三者皆不存在時才 fallback 到 .bashrc）。
#     若存在其中之一且未 source .bashrc，自動啟用會在登入 shell 中靜默失效。
# ------------------------------------------------------------------------------
$loginProfilePath = $null
foreach ($name in '.bash_profile', '.bash_login', '.profile') {
    $candidate = Join-Path $HOME $name
    if (Test-Path $candidate) { $loginProfilePath = $candidate; break }
}
if ($loginProfilePath) {
    # 啟發式：內容提及 bashrc 即視為已處理，避免落入過於複雜的二次 source 判斷
    $loginContent = Read-Utf8Text $loginProfilePath
    if ($loginContent -notmatch 'bashrc') {
        $loginInner = @'
# 由 shell-env.ps1 加入：讓 login shell（Git Bash 預設）載入 ~/.bashrc
if [ -f "$HOME/.bashrc" ]; then . "$HOME/.bashrc"; fi
'@ -replace "`r`n", "`n"
        $loginUpdated = Set-ManagedBlock -Append -Text $loginContent -StartMarker '# === BASH_LOGIN_SOURCE_BASHRC_START ===' -EndMarker '# === BASH_LOGIN_SOURCE_BASHRC_END ===' -Inner $loginInner
        [System.IO.File]::WriteAllText($loginProfilePath, $loginUpdated, $utf8NoBom)
        Write-Host "[Bash] 已附加 source ~/.bashrc 至 $loginProfilePath。" -ForegroundColor Green
    } else {
        Write-Host "[Bash] $loginProfilePath 已載入 ~/.bashrc，跳過。" -ForegroundColor Yellow
    }
}

# ------------------------------------------------------------------------------
# 6. 設定 BASH_ENV 使用者環境變數
#    非互動式 bash（如 AI agent 的 bash 工具）不讀 ~/.bashrc，
#    只有在環境變數 BASH_ENV 指向本檔時才會載入
# ------------------------------------------------------------------------------
$currentBashEnv = [Environment]::GetEnvironmentVariable('BASH_ENV', 'User')

if ($currentBashEnv -ne $bashrcPath) {
    if (-not [string]::IsNullOrEmpty($currentBashEnv)) {
        Write-Host "[環境變數] 注意：使用者原有的 BASH_ENV = $currentBashEnv 已被覆蓋。" -ForegroundColor Yellow
    }
    [Environment]::SetEnvironmentVariable('BASH_ENV', $bashrcPath, 'User')
    Write-Host "[環境變數] 已設定 BASH_ENV = $bashrcPath（僅對之後新誕生的程序生效）" -ForegroundColor Green
    Write-Host "         所有非互動式 bash（腳本、hooks、cron）都會載入 .bashrc 並依 cwd 自動切換 venv。" -ForegroundColor Gray
} else {
    Write-Host "[環境變數] BASH_ENV 已正確指向 ~/.bashrc，跳過。" -ForegroundColor Yellow
}

Write-Host "`n安裝完成！請完整重開 PowerShell 與 Git Bash（若有終端機宿主如 VSCodium 也需整個重啟）即刻生效。" -ForegroundColor Cyan
