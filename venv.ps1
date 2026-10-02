# ==============================================================================
# PowerShell 雙 Shell 自動化安裝腳本
# 一次性設定：PowerShell ($PROFILE) + Bash (~/.bashrc) + BASH_ENV 環境變數
# ==============================================================================

function Update-MarkedBlock {
    param(
        [string]$Content,
        [string]$StartMarker,
        [string]$EndMarker,
        [string]$NewBlock
    )
    if ($Content -and $Content -match [regex]::Escape($StartMarker)) {
        $pattern = '(?s)' + [regex]::Escape($StartMarker) + '.*?' + [regex]::Escape($EndMarker)
        return [regex]::Replace($Content, $pattern, $NewBlock.TrimEnd("`r", "`n"))
    }
    if ([string]::IsNullOrEmpty($Content)) {
        return $NewBlock.TrimStart("`r", "`n")
    }
    return $Content.TrimEnd("`r", "`n") + "`r`n`r`n" + $NewBlock.TrimStart("`r", "`n")
}

# ------------------------------------------------------------------------------
# 1. 寫入 PowerShell 設定檔 ($PROFILE)
# ------------------------------------------------------------------------------
$psProfilePath = $PROFILE
$psMarker = "# === NATIVE_VENV_AUTO_ACTIVATE_START ==="
$psEndMarker = "# === NATIVE_VENV_AUTO_ACTIVATE_END ==="

if (-not (Test-Path (Split-Path $psProfilePath))) { New-Item -ItemType Directory -Force -Path (Split-Path $psProfilePath) | Out-Null }
if (-not (Test-Path $psProfilePath)) { New-Item -ItemType File -Force -Path $psProfilePath | Out-Null }

$psContent = Get-Content $psProfilePath -Raw -ErrorAction SilentlyContinue
$psExisted = $psContent -and $psContent -match [regex]::Escape($psMarker)

$psCode = @"
$psMarker
# 1. 歷史紀錄最佳化 (忽略重複與空白)
Set-PSReadLineOption -HistoryNoDuplicates
Set-PSReadLineOption -AddToHistoryHandler {
    param([string]`$line)
    if ([string]::IsNullOrWhiteSpace(`$line)) { return `$false }
    if (`$line.StartsWith(" ")) { return `$false }
    return `$true
}

# 2. 原生極速虛擬環境切換引擎
# 回傳 `$true 表示目前 PATH 確實指向指定 venv（避免只有 VIRTUAL_ENV、PATH 未切的半啟動）
function global:_Venv-Is-Live {
    param([string]`$venvPath, [string]`$binDir)
    if ([string]::IsNullOrEmpty(`$venvPath)) { return `$false }
    if (`$env:VIRTUAL_ENV -ne `$venvPath) { return `$false }
    `$newBin = Join-Path `$venvPath `$binDir
    `$pathSep = [System.IO.Path]::PathSeparator
    `$first = (`$env:PATH -split [regex]::Escape(`$pathSep) | Select-Object -First 1)
    if (`$first -eq `$newBin) { return `$true }
    try {
        return ([IO.Path]::GetFullPath(`$first) -eq [IO.Path]::GetFullPath(`$newBin))
    } catch {
        return `$false
    }
}

function global:Auto-Activate-Venv {
    `$current = `$PWD.Path
    `$venvPath = `$null

    `$isWin = [Environment]::OSVersion.Platform -eq 'Win32NT'
    `$binDir = if (`$isWin) { "Scripts" } else { "bin" }
    `$exeName = if (`$isWin) { "python.exe" } else { "python" }
    `$pathSep = [System.IO.Path]::PathSeparator

    # 向上遞迴尋找虛擬環境
    while (![string]::IsNullOrEmpty(`$current)) {
        if (Test-Path (Join-Path `$current ".venv\`$binDir\`$exeName")) {
            `$venvPath = Join-Path `$current ".venv"; break
        }
        if (Test-Path (Join-Path `$current "venv\`$binDir\`$exeName")) {
            `$venvPath = Join-Path `$current "venv"; break
        }
        `$parent = Split-Path `$current -Parent
        if (`$parent -eq `$current) { break }
        `$current = `$parent
    }

    # 已真正啟用同一環境 -> 跳過
    if (_Venv-Is-Live -venvPath `$venvPath -binDir `$binDir) { return }

    # 退出舊環境：從 PATH 中拔除舊的 bin/Scripts 路徑
    if (`$env:VIRTUAL_ENV) {
        `$oldBin = Join-Path `$env:VIRTUAL_ENV `$binDir
        `$paths = `$env:PATH -split [regex]::Escape(`$pathSep) | Where-Object { `$_ -ne `$oldBin }
        `$env:PATH = `$paths -join `$pathSep
        `$env:VIRTUAL_ENV = `$null
    }

    # 進入新環境：把新的 bin/Scripts 路徑塞入 PATH 最前方
    if (`$venvPath) {
        `$env:VIRTUAL_ENV = `$venvPath
        `$newBin = Join-Path `$venvPath `$binDir
        `$env:PATH = "`$newBin`$pathSep`$env:PATH"
    }
}

# 3. 畫面渲染攔截 (內建 Oh-My-Posh 快取；每次 prompt 都檢查半啟動)
`$global:_venv_has_omp = [bool](Get-Command oh-my-posh -ErrorAction SilentlyContinue)

if (-not `$global:_venv_original_prompt) {
    if (Test-Path "Function:\prompt") { `$global:_venv_original_prompt = `$function:prompt }
    else { `$global:_venv_original_prompt = { "PS `$(`$executionContext.SessionState.Path.CurrentLocation)> " } }
}

function global:prompt {
    Auto-Activate-Venv

    if (-not `$global:_venv_has_omp) {
        if (`$env:VIRTUAL_ENV) {
            `$venvName = Split-Path `$env:VIRTUAL_ENV -Leaf
            Write-Host -NoNewline -ForegroundColor Green "(`$venvName) "
        }
    }

    & `$global:_venv_original_prompt
}

# Profile 載入時直接執行一次（非互動式 / Agent 終端不會畫 prompt）
Auto-Activate-Venv
$psEndMarker
"@

$psUpdated = Update-MarkedBlock -Content $psContent -StartMarker $psMarker -EndMarker $psEndMarker -NewBlock $psCode
Set-Content -Path $psProfilePath -Value $psUpdated -Encoding UTF8
if ($psExisted) {
    Write-Host "[PowerShell] 已更新 `$PROFILE 內的 venv 自動啟動區塊。" -ForegroundColor Green
} else {
    Write-Host "[PowerShell] 已成功寫入 `$PROFILE！" -ForegroundColor Green
}

# ------------------------------------------------------------------------------
# 2. 寫入 Bash 設定檔 (~/.bashrc)
# ------------------------------------------------------------------------------
$bashrcPath = Join-Path $HOME ".bashrc"
$bashMarker = "# === BASH_VENV_AUTO_ACTIVATE_START ==="
$bashEndMarker = "# === BASH_VENV_AUTO_ACTIVATE_END ==="

if (-not (Test-Path $bashrcPath)) { New-Item -ItemType File -Force -Path $bashrcPath | Out-Null }

$bashContent = Get-Content $bashrcPath -Raw -ErrorAction SilentlyContinue
$bashExisted = $bashContent -and $bashContent -match [regex]::Escape($bashMarker)

$bashCode = @'
# === BASH_VENV_AUTO_ACTIVATE_START ===
# 自動偵測並啟動/退出虛擬環境
# 回傳 0 表示目前 python 確實來自指定 venv（避免只有 VIRTUAL_ENV、PATH 未切的半啟動）
_venv_is_live() {
    local venv="$1"
    local py
    [ -n "$venv" ] || return 1
    [ "$VIRTUAL_ENV" = "$venv" ] || return 1
    py="$(command -v python 2>/dev/null)" || return 1
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
    while [ -n "$target_dir" ] && [ "$target_dir" != "/" ]; do
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
        target_dir="$(dirname "$target_dir")"
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
# 註冊至 PROMPT_COMMAND
if [[ ";;$PROMPT_COMMAND;" != *";_venv_prompt_command;"* ]]; then
    PROMPT_COMMAND="_venv_prompt_command${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
fi
# 非互動式 shell（如 AI agent 的 bash 工具）不會跑 PROMPT_COMMAND，
# 且可能透過 BASH_ENV 直接載入本檔，故在載入時主動觸發一次 venv 自動啟動
auto_activate_venv
# === BASH_VENV_AUTO_ACTIVATE_END ===
'@

# 將換行字元轉為 Unix (LF)，避免 Git Bash 報錯 `\r` 錯誤
$bashCodeUnix = $bashCode -replace "`r`n", "`n" -replace "`r", "`n"
# bashrc 以 LF 為主；讀取後若為 CRLF，先正規化再替換
if ($bashContent) {
    $bashContentNorm = $bashContent -replace "`r`n", "`n" -replace "`r", "`n"
} else {
    $bashContentNorm = ""
}
$bashUpdated = Update-MarkedBlock -Content $bashContentNorm -StartMarker $bashMarker -EndMarker $bashEndMarker -NewBlock $bashCodeUnix.TrimStart("`n")
$bashUpdatedUnix = $bashUpdated -replace "`r`n", "`n" -replace "`r", "`n"
[System.IO.File]::WriteAllText($bashrcPath, $bashUpdatedUnix, [System.Text.UTF8Encoding]::new($false))
if ($bashExisted) {
    Write-Host "[Bash] 已更新 $bashrcPath 內的 venv 自動啟動區塊。" -ForegroundColor Green
} else {
    Write-Host "[Bash] 已成功寫入 $bashrcPath ！" -ForegroundColor Green
}

# ------------------------------------------------------------------------------
# 3. 設定 BASH_ENV 使用者環境變數
#    非互動式 bash（如 AI agent 的 bash 工具）不讀 ~/.bashrc，
#    只有在環境變數 BASH_ENV 指向本檔時才會載入
# ------------------------------------------------------------------------------
$bashrcWinPath = "$HOME\.bashrc"
$currentBashEnv = [Environment]::GetEnvironmentVariable('BASH_ENV', 'User')

if ($currentBashEnv -ne $bashrcWinPath) {
    [Environment]::SetEnvironmentVariable('BASH_ENV', $bashrcWinPath, 'User')
    Write-Host "[環境變數] 已設定 BASH_ENV = $bashrcWinPath（僅對之後新誕生的程序生效）" -ForegroundColor Green
} else {
    Write-Host "[環境變數] BASH_ENV 已正確指向 ~/.bashrc，跳過。" -ForegroundColor Yellow
}

# 清理舊版架構遺留的 ~/.bash_env 包裝檔
$legacyBashEnv = Join-Path $HOME ".bash_env"
if (Test-Path $legacyBashEnv) {
    Remove-Item $legacyBashEnv -Force
    Write-Host "[清理] 已刪除舊版遺留檔 ~/.bash_env" -ForegroundColor Green
}

Write-Host "`n安裝完成！請完整重開 PowerShell 與 Git Bash（若有終端機宿主如 VSCodium 也需整個重啟）即刻生效。" -ForegroundColor Cyan
