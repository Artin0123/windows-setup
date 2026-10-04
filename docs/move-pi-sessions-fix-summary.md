# move-pi-sessions.cs 修正摘要

## 原始問題

拖入 `C:\Users\artin\.pi\agent\sessions\--C--Users-artin-Sync-coding-zenasr--` 當目標時，原本會算成巢狀目錄名 `--C--Users-artin-.pi-agent-sessions---C--Users-artin-Sync-coding-zenasr----`。

根因是解碼把每個 `-` 都當 `\`，而且 `ClassifyDrop` 只看名稱、不看 `sessionsRoot`。現在解碼改成對檔案系統回溯驗證。拖入的 session 資料夾也會直接記為 `_targetDir`，不再重新編碼。`C:\Users\artin\Sync\coding\zenasr` 能正確還原。

只修改 `move-pi-sessions.cs`。沒有 commit，沒有重建 exe，沒有動其他 repo 檔案。

## 各項修改

### Bug（B）

- **B-1 / B-2**：`DecodeSessionDirName(name, out inferred)` 由左到右回溯。每個 `-` 試「分隔符」或「名稱一部分」，取第一個 `Directory.Exists` 成立的完整路徑（inferred=false）。走不通時退回「檔案系統確認過的最深一層，其餘片段當 `\`」，空片段略過（不再產生 `my\\app`），並標 inferred=true。`DriveRe` 改成 `(.*)`，`--D----` 解成 `D:\`。確認對話框在 cwd 只來自名稱推測時多一行警告，`ClassifyDrop` 新增 `out bool inferred`，舊簽名保留為包裝。
- **B-3**：`ClassifyDrop` 真的使用 `sessionsRoot`。父資料夾是 sessions 根目錄，或資料夾內有 header 為 `type:session` 的 jsonl（`HasSessionHeader`），才算 session 資料夾。
- **B-4**：拖入的 session 資料夾存為 `_targetDir`，`TryConvert` 資料夾還在就直接用，否則退回 `FindTargetDir`。對話框新增「目標目錄」，`ClearTarget` 一併清除。
- **B-5**：`MoveOne` 開頭 dst 已存在就拒絕，寫入用不覆蓋的 `File.Move`，競爭下 dst 剛出現也不會被蓋掉。
- **B-6 / B-7**：`MoveOne` 改用位元組掃描找到 header 行的確切範圍，只重寫該行，前後位元組串流原樣複製。BOM、CRLF、非 UTF-8 位元組都保留。header 行本身含壞位元組且需要改寫時拒絕搬移。來源以 `FileShare.Read` 開啟，pi 正在寫的檔仍會被拒絕。
- **B-8**：`PathKey` 對空字串或無效路徑回傳 null，新增 `SameKey(a,b)`（雙方有效且相等才 true），所有呼叫端改用它。
- **B-9**：`RunMove` 用 try/finally 重設 `_working`、`SyncPathRows`、`RefreshHint`，結果訊息在 finally 之後才跳出。
- **B-10**：`OnDragDrop` 改用 `BeginInvoke`。

### 冗餘（R）

- **R-1**：由 B-3 使用 `sessionsRoot`。
- **R-2**：Decode 註解寫明有損與驗證方式。
- **R-3**：`TryParseSessionHeader` 與 `ScanHeader` 共用，`RewriteHeaderCwd` 在 `MoveOne` 只處理 header 行。
- **R-4**：移除 `seen` HashSet 與 `.tmp` 結尾判斷，保留 `.tmp-` 前綴判斷。
- **R-5**：固定樣式 Regex 改 `static readonly`。
- **R-6**：`TryParseLooseJson` 移除多餘 try/catch。
- **R-7**：抽出 `SortedJsonl`、`EnumerateHeaders`，供 `DirExampleCwd`、`HasSessionHeader`、`CollectFiles` 共用。
- **R-8**：字串比較改 `OrdinalIgnoreCase`，不再 `ToLowerInvariant`。
- **R-9**：PID 改 `static readonly`，Process 物件 Dispose。
- **R-11**：移除 `ClassifyDrop` 重複的 `isSource=false`，非 session 分支 cwd 改回 `NormalizeCwd(full)`。
- **R-12**：`Config.Load` 補欄位時沿用已讀的 `raw`。
- **C14f**：`LooksLikeSessionDirName` 要求中間至少一個非 `-` 字元。

### 效能（P）

- **P-1**：`ReadHeader` 不再配置大緩衝或整段 Split，改用與 `MoveOne` 共用的位元組掃描器。上限統一為 `HeaderScanLines=200` 與單行 `HeaderMaxLineLength`（4M 字元，超過的行跳過），`ReadHeader`、`MoveOne`、`RewriteHeaderCwd` 範圍一致。
- **P-2**：`FindSessionDirs` 第三 pass 每資料夾只用 `DirExampleCwd`。
- **P-3 / P-4**：`MoveOne` 串流複製，記憶體與檔案大小無關。驗證用記憶體中的新 header，寫完只比對檔案長度，不再重讀 512KB。
- **P-5**：`AtomicWrite(path, writer, overwrite)` 在 overwrite 且目標存在時用 `File.Replace`，否則用 `File.Move`。
- **P-6**：`WrapPath` 改二分搜尋，超過 `MaxPathLines` 行即停止。

### GUI（第 6 節）

- `ApplyDropToNextSlot` 在 `_working` 時直接 return。
- 全部搬完且沒有任何失敗時，自動清空來源。有失敗就保留來源，方便重試。
- 選到或拖入 sessions 根目錄本身時跳警告，且不設定。
- 確認對話框新增「目標目錄」與推測警告行。

## 測試結果

- **編譯**：內建 `csc.exe`（Framework64 v4.0.30319，C# 5），`/target:winexe`，0 錯誤。檔內沒有 C# 6 語法（`$"..."`、`?.`、`nameof`、`out var`、expression-bodied 成員）。
- **Coder 的 harness**：77 項通過，0 失敗。涵蓋原始 bug、解碼各案例（C1..C14f）、路徑（S/F/T）、改寫（R1..R11）、搬移（B1、B2、M1..M10、P5）、K1/K2。
- **Reviewer 獨立重編並重跑**：自寫的丟棄式 harness 67 項通過，0 失敗，無新回歸。這個數字和 coder 的 77 不同，是因為兩邊各自的 harness 案例集不同，不是有案例失敗。
- **效能（coder 量測）**：
  - `FindSessionDirs` 3000 檔無命中：55 ms，gen2 GC 0 次（基準 856 ms / 480 次）。
  - `MoveOne` 約 97MB 檔：116～143 ms，gen2 GC 0 次，peak working set 26MB（基準 818 ms / 15 次 gen2）。
- **審查結論**：APPROVED。

## 未驗證的部分

GUI 流程只有編譯、讀碼與啟動冒煙（行程存活 3 秒）。`_targetDir` 選用、確認對話框警告行、自動清空、根目錄警告、`BeginInvoke`、`_working` 檢查都沒有實際拖放操作過。C12b 在 `PiPaths` 層確認過 `ClassifyDrop` 保留拖入資料夾，且與重新編碼的結果不同，所以 `TryConvert` 選用 `_targetDir` 的分支是必要的。建議合併前手動拖放跑一次。

## 刻意略過的項目與原因

- **ReadHeader 不用 `StreamReader.ReadLine`**：`ReadLine` 無法限制單行長度，也拿不到 header 的位元組偏移，而 `MoveOne` 的串流複製需要偏移。改用共用的位元組掃描器。
- **R-8「列舉一次後傳入」沒做**：`TryConvert` 的兩次目錄列舉只是一次 `GetDirectories`，相比讀檔頭可忽略，為此改公開簽名不划算。
- **R-5 的 `Config.UpsertStatic` 維持動態 Regex**：它依 key 動態建立，且只在存設定時執行。
- **R-10 保留 `AtomicWrite` finally 刪暫存檔**：失敗路徑需要。
- **`FindSessionDirs` 名稱比對**：改成單次 `OrdinalIgnoreCase`，不再先精確後不分大小寫。NTFS 預設不分大小寫，結果相同。
- **`HasSessionHeader` 對任意拖入資料夾會讀 jsonl 檔頭**：直到命中為止。一般專案資料夾沒有 jsonl，成本可忽略，不加掃描檔數限制。
- **UNC session 資料夾無 jsonl 時解碼回 null（C11e）**：行為保守，GUI 會提示無法解析，不修改。
- **沒有需要使用者決定的行為變更**：對話框多出的「目標目錄」與警告行是任務明確要求的。
