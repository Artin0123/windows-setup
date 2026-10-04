# move-pi-sessions.cs 修正驗證筆記

只改了 `move-pi-sessions.cs`。沒有 commit、沒有動 `move_pi_sessions.exe`、沒有執行 `csc-build.cmd`。GUI 外觀、ini 格式、icon 程式碼未動。

## 實際執行的驗證

1. 編譯（C# 5 內建 csc，Framework64 v4.0.30319）：
   `csc /nologo /target:winexe /out:...\mps-fix-tmp\chk.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll move-pi-sessions.cs` → 0 錯誤 0 警告。
2. Test harness（丟棄式，`/main:MovePiSessions.Harness`，與本檔一起編譯，直接呼叫 internal `PiPaths`，不開 GUI）→ **77 項通過、0 失敗**。
3. GUI 冒煙：啟動 chk.exe，3 秒後行程仍存活（視窗能建起來），之後手動結束。只證明能啟動，**沒有實測**拖放、確認對話框、搬移流程。
4. 收尾：刪除整個 `mps-fix-tmp`；`git status` 只應顯示 `move-pi-sessions.cs` 被修改與原本就有的未追蹤 `.agents/`。

## 每個項目改了什麼

| ID | 改動 |
|---|---|
| B-1/B-2 | `DecodeSessionDirName(name, out inferred)`：由左到右回溯，每個 `-` 試「分隔符」或「名稱的一部分」，取第一個 `Directory.Exists` 成立的完整路徑（inferred=false）。走不通時退路為「檔案系統確認過的最深一層 + 其餘片段全當 `\`」，空片段略過（不再產生 `my\\app`），inferred=true。`DriveRe` 改成 `(.*)`，所以 `--D----` 解成 `D:\`（原本是 null）。單參數版本保留。 |
| B-1 警告 | `ClassifyDrop` 多一個 `out bool inferred`（舊 5 參數版本保留為包裝）。MainForm 存 `_sourceInferred/_targetInferred`，確認對話框在 cwd 只來自名稱推測時多一行「注意：…可能不準」。 |
| B-4 | 拖入 session 資料夾當目標時存 `_targetDir`；`TryConvert` 該資料夾還在就直接用，不再 `FindTargetDir` 重新編碼。確認對話框新增「目標目錄」。`ClearTarget` 一併清除。 |
| B-3 | `ClassifyDrop` 真的使用 `sessionsRoot`：父資料夾等於 sessions 根目錄，或資料夾內有 header 為 type:session 的 jsonl（`HasSessionHeader`），才算 session 資料夾。只看名稱不再算。 |
| B-5 | `MoveOne` 開頭 dst 已存在就拒絕；寫入時用 `File.Move`（不覆蓋），競爭下 dst 剛好出現也會丟 IOException 而不是蓋掉。 |
| B-9 | `RunMove` 用 try/finally 重設 `_working`、`SyncPathRows`、`RefreshHint`；結果訊息在 finally 之後才跳，所以彈窗時狀態已復原。 |
| B-6/B-7/P-3/P-4 | `MoveOne` 重寫：`ScanHeader` 以位元組逐行掃描，找到 header 行的確切位元組範圍；只重寫該行，前後位元組用串流原樣複製（保留 BOM、非 UTF-8 位元組；記憶體與檔案大小無關）。來源以 `FileShare.Read` 開啟，pi 正在寫入的檔案開不起來，仍被拒絕。驗證改用記憶體中的新 header（不重讀 512KB），寫完只比對檔案長度。header 行本身含非法 UTF-8 且需要改寫時拒絕搬移（避免悄悄損毀）。 |
| ReadHeader | 與 `MoveOne`、`RewriteHeaderCwd` 共用同一組上限：`HeaderScanLines=200`、`HeaderMaxLineLength`（單行 4M 字元，超過的行跳過）。移除 512KB 字元上限與 1MB 緩衝。 |
| B-8 | `PathKey` 空字串/無效路徑回傳 null；新增 `SameKey(a,b)`（雙方有效且相等才 true），所有呼叫端改用它。 |
| B-10 | `OnDragDrop` 改 `BeginInvoke`。 |
| P-1 | 見 ReadHeader：不再配置大緩衝、不再對整段 Split。 |
| P-2 | `FindSessionDirs` 第三 pass 每資料夾只用 `DirExampleCwd`。 |
| P-5 | `AtomicWrite(path, writer, overwrite)`：overwrite 且目標存在時用 `File.Replace`，否則 `File.Move`。 |
| P-6 | `WrapPath` 改二分搜尋，超過 `MaxPathLines` 行即停止。 |
| R-1 | 由 B-3 用上。 |
| R-2 | Decode 註解寫明有損與驗證方式。 |
| R-3 | `TryParseSessionHeader` 與 `ScanHeader` 共用；`RewriteHeaderCwd` 在 MoveOne 只處理 header 那一行。 |
| R-4 | 移除 `seen` HashSet 與 `.tmp` 結尾判斷，保留 `.tmp-` 前綴判斷（K1 要求）。 |
| R-5 | 所有固定樣式 Regex 改成 `static readonly`（`UpsertStatic` 依 key 動態建立，未改）。 |
| R-6 | `TryParseLooseJson` 移除多餘 try/catch。 |
| R-7 | 抽出 `SortedJsonl` 與 `EnumerateHeaders`，`DirExampleCwd`/`HasSessionHeader`/`CollectFiles` 共用。 |
| R-8 | 字串比較改 `OrdinalIgnoreCase`，不再 `ToLowerInvariant`。 |
| R-9 | PID 改 `static readonly`，Process 物件 Dispose。 |
| R-11 | 移除 ClassifyDrop 重複的 `isSource=false`；非 session 分支的 cwd 改回傳 `NormalizeCwd(full)`（C7）。 |
| R-12 | `Config.Load` 補欄位時沿用已讀的 `raw`。 |
| C14f | `LooksLikeSessionDirName` 要求中間至少一個非 `-` 字元。 |
| GUI 6 | `ApplyDropToNextSlot` 在 `_working` 時 return；全部搬完（無失敗）後自動清空來源；選到/拖入 sessions 根目錄本身時跳警告並不設定。 |

## 測試結果（77 PASS / 0 FAIL）

- 原始 bug：ORIG-empty、C1、C8、ORIG-no-nesting、ORIG-nested-name-not-produced 通過（拖入 `--C--Users-artin-Sync-coding-zenasr--` 還原為 `C:\Users\artin\Sync\coding\zenasr`，不會產生巢狀名）。
- 分類/解碼：C2、C3-nonexistent（退路，inferred=true）、C3-with-my-app（fs 驗證，得 `...\x\my-app`，inferred=false）、C3-ambiguous（同時有 `x\my` 與 `x\my-app`）、C3b、C4、C5、C6、C7、C9、C10、C10b、B3-header-outside-root、B3-non-session-jsonl、C11b、C11c、C11d、C11e、C12、C12b、C13、C14、C14b、C14c、C14d、C14d-fs、C14e、C14f。
- 路徑：S1、S3、S4、F2、F3、F3b、F4、T2、ROOT-self。
- 改寫：R1–R11 全通過。
- 搬移：B1（header 在 1MB 之後）、B2（600KB 單行 header）、M1（保留 BOM+CRLF，位元組逐一比對）、M2/M2b（非 UTF-8 位元組逐位元組保留）、M2c（header 行含壞位元組 → 拒絕，來源保留）、M3、M4、M5、M5b、M6、M7、M8、M9b、M10、no-tmp-left、OK-no-trailing-newline、P5-replace、P5-no-overwrite。
- K1/K2 通過。
- 效能：`FindSessionDirs` 3000 檔無命中 55 ms、gen2 GC 0 次（基準 856 ms / 480 次）；`MoveOne` 92MiB（約 97MB）檔 143 ms（另一次 116 ms）、gen2 GC 0 次、行程 peak working set 26MB（基準 818 ms / 15 次 gen2）。

## 刻意略過或需知道的事

- **ReadHeader 沒有用 `StreamReader.ReadLine`**：`ReadLine` 無法限制單行長度（超長行會整行讀進記憶體），也拿不到 header 的位元組偏移，而 MoveOne 的串流複製需要偏移。改用與 MoveOne 共用的位元組掃描器，三者範圍因此一致，達成原本「共用上限、掃描範圍一致」的目的。
- **R-8 的「列舉一次後傳入」沒做**：`TryConvert` 的兩次目錄列舉只是一次 `GetDirectories`，相比讀檔頭可忽略；為此改公開簽名不划算。
- **R-5 的 `Config.UpsertStatic`** 維持動態 Regex（只在存設定時執行）。
- **R-10** `AtomicWrite` 的 finally 刪暫存檔保留（失敗路徑需要）。
- **FindSessionDirs** 名稱比對改為單次 `OrdinalIgnoreCase`，不再先精確後不分大小寫；NTFS 預設不分大小寫，結果相同。
- **自動清空來源**只在「至少搬成功一個且沒有任何失敗」時執行；有失敗就保留來源，讓使用者處理後可直接重試。
- **HasSessionHeader** 對非 sessions 底下的資料夾會依序讀該資料夾 jsonl 的檔頭直到找到 session header；一般專案資料夾通常沒有 jsonl，成本可忽略。
- **GUI 流程（_targetDir、確認對話框警告、自動清空、根目錄警告、BeginInvoke、_working 檢查）只有編譯與啟動冒煙驗證，沒有自動化或手動操作測試**。C12b 在 PiPaths 層驗證了 `ClassifyDrop` 保留拖入資料夾且與重新編碼結果不同；`TryConvert` 選用 `_targetDir` 的分支靠讀碼確認。
- 沒有需要使用者決定的行為變更；確認對話框多了「目標目錄」與（必要時）警告行，這是任務明確要求的。
