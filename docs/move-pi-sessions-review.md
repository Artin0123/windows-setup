# move-pi-sessions.cs 實測與 Code Review 報告

範圍：`move-pi-sessions.cs`（含「拖入 session 資料夾當目標」的修補）。
方式：用 Windows 內建 csc（C# 5）把本檔與 test harness 一起編譯（`/main:` 指定 harness 入口），直接呼叫 `PiPaths` 的靜態方法，在測試資料夾建假的 sessions 結構實跑。沒有啟動 GUI。
GUI 流程（第 6 節）只做讀碼分析，未實測。
本檔沒有被修改。測試資料夾與編譯產物已刪除，repo 內沒有殘留。

## 結論摘要

- 使用者回報的 bug 已修好。拖入 `--C--Users-artin-Sync-coding-zenasr--` 會還原成 `C:\Users\artin\Sync\coding\zenasr`（C1、C8 通過），不再產生巢狀目錄名。
- 修補後仍有 4 個真實問題，主要是「有損解碼」與「誤判」：
  1. 空的 session 資料夾解碼會把目錄名裡的 `-` 當成 `\`，寫進 jsonl 的 cwd 是錯的（高）。
  2. 名稱以 `--` 開頭結尾的一般專案資料夾會被當成 session 資料夾，可能無法當目標，或被還原成怪路徑（中）。
  3. 拖入的 session 資料夾內 jsonl cwd 與目錄名不一致時，檔案會被搬到另一個資料夾，不是使用者拖入的那個（中）。
  4. 原本就存在的問題：`MoveOne` 會覆蓋已存在的目標檔、會破壞非 UTF-8 位元組、header 超過 512KB 字元時驗證失敗並留下兩份檔案（中）。

## 1. 測試案例表

「實際」欄為實跑輸出。路徑中的 `<T>` 代表測試用暫存資料夾。

### 1.1 ClassifyDrop / Encode / Decode

| ID   | 輸入                                                                            | 預期                                                   | 實際                                      | 通過                                                                     |
| ---- | ------------------------------------------------------------------------------- | ------------------------------------------------------ | ----------------------------------------- | ------------------------------------------------------------------------ |
| C1   | `<T>\sessions\--C--Users-artin-Sync-coding-zenasr--`（有 jsonl，檔頭 cwd 正確） | isSource=True, cwd=`C:\Users\artin\Sync\coding\zenasr` | 相同                                      | 通過                                                                     |
| C2   | 空 session 資料夾 `--C--...-zenasr2--`                                          | 靠目錄名解碼得 `C:\Users\artin\Sync\coding\zenasr2`    | 相同                                      | 通過                                                                     |
| C3   | 空 session 資料夾 `--C--x-my-app--`（真實專案是 `C:\x\my-app`）                 | `C:\x\my-app`                                          | `C:\x\my\app`                             | **失敗**                                                                 |
| C3b  | 同上但有 jsonl（檔頭 cwd=`C:\x\my-app2`）                                       | `C:\x\my-app2`                                         | 相同                                      | 通過                                                                     |
| C4   | 空資料夾 `--C--Users-artin-.pi-agent--`（帶點）                                 | `C:\Users\artin\.pi\agent`                             | 相同                                      | 通過                                                                     |
| C5   | 空資料夾 `--C--a_b-c_d--`（底線）                                               | `C:\a_b\c_d`                                           | 相同                                      | 通過                                                                     |
| C6   | 一般專案資料夾                                                                  | isSource=False, cwd=完整路徑                           | 相同                                      | 通過                                                                     |
| C7   | 一般專案資料夾 + 結尾 `\`                                                       | 不含結尾斜線                                           | ClassifyDrop 回傳的 cwd 仍含結尾 `\`      | 不影響：非 session 分支的呼叫端改用 `NormalizeCwd(path)`，不使用這個 cwd |
| C8   | session 資料夾 + 結尾 `\`                                                       | 同 C1                                                  | 相同                                      | 通過                                                                     |
| C9   | 磁碟機根目錄 `C:\`                                                              | isSource=False, cwd=`C:\`                              | 相同                                      | 通過                                                                     |
| C10  | 專案資料夾名為 `--notsession--`（不在 sessions 底下）                           | isSource=False                                         | isSource=True, cwd=null                   | **失敗**                                                                 |
| C10b | 專案資料夾名為 `--C--real--`（不在 sessions 底下）                              | isSource=False                                         | isSource=True, cwd=`C:\real`              | **失敗**                                                                 |
| C11  | UNC `\\localhost\c$\Windows`                                                    | isSource=False, cwd 不變                               | 相同                                      | 通過                                                                     |
| C11b | Encode(`\\srv\share\p`)                                                         | `---srv-share-p--`（與 pi 的規則一致）                 | 相同                                      | 通過                                                                     |
| C11c | Encode(`D:\`)                                                                   | `--D----`                                              | 相同                                      | 通過                                                                     |
| C11d | Decode(`--D----`)（磁碟機根目錄的 session 資料夾）                              | `D:\`                                                  | null                                      | **失敗**（無法還原，只能靠 jsonl）                                       |
| C11e | Decode(`---srv-share-p--`)（UNC 的 session 資料夾）                             | `\\srv\share\p`                                        | null                                      | **失敗**（同上）                                                         |
| C12  | session 資料夾 `--C--old-name--`，jsonl 檔頭 cwd=`C:\other\place`               | 用 jsonl cwd                                           | `C:\other\place`                          | 通過                                                                     |
| C12b | 承 C12，以還原的 cwd 呼叫 `FindTargetDir`                                       | 仍指向使用者拖入的 `--C--old-name--`                   | 指向 `--C--other-place--`（另一個資料夾） | **失敗**                                                                 |
| C13  | 第一個 jsonl 損壞，第二個有 header                                              | 讀到第二個的 cwd                                       | `C:\multi`                                | 通過                                                                     |
| C14  | Decode(`--C--x-my-app--`)                                                       | `C:\x\my-app`                                          | `C:\x\my\app`                             | **失敗**（有損編碼）                                                     |
| C14b | Encode(Decode(name)) 是否等於 name                                              | 等於                                                   | 等於                                      | 通過（目錄名穩定，但 cwd 錯）                                            |
| C14c | 小寫磁碟機 `--c--x--`                                                           | `C:\x`                                                 | 相同                                      | 通過                                                                     |
| C14d | Decode(`--C--x-my--app--`)                                                      | `C:\x\my--app`                                         | `C:\x\my\\app`（雙反斜線，不是合法路徑）  | **失敗**                                                                 |
| C14e | Decode(`--foo--`)                                                               | null                                                   | null                                      | 通過                                                                     |
| C14f | `LooksLikeSessionDirName("-----")`                                              | False                                                  | True（之後 Decode 回 null，無實害）       | 失敗（輕微）                                                             |

### 1.2 PathKey / FindTargetDir / FindSessionDirs / 同目錄判斷

| ID  | 輸入                                                              | 預期                         | 實際          | 通過                             |
| --- | ----------------------------------------------------------------- | ---------------------------- | ------------- | -------------------------------- |
| S1  | `C:\A\b\` vs `c:\a\B`                                             | 相同 key                     | 相同          | 通過                             |
| S3  | PathKey(`C:\a                                                     | b`)（含非法字元）            | 空字串        | 空字串                           | 通過           |
| S4  | PathKey(`""`) == PathKey(`C:\a                                    | b`)                          | False         | True（兩個「無效」會被當成相等） | **失敗**（低） |
| F2  | `FindTargetDir(root, "c:\USERS")`，不存在                         | `root\--c--USERS--`          | 相同          | 通過                             |
| F3  | `FindTargetDir(root, "c:\users\foo")`，已有 `--C--Users-Foo--`    | 重用大小寫不同的既有資料夾   | 相同          | 通過                             |
| F4  | `FindSessionDirs(root, "C:\other\place")`（目錄名對不上，靠檔頭） | 找到 `--C--old-name--`       | 相同          | 通過                             |
| T1  | 目標為空 session 資料夾 `--C--w-my-app--`                         | `FindTargetDir` 回同一資料夾 | 同一資料夾    | 通過                             |
| T1b | 承 T1，寫進 jsonl 檔頭的 cwd                                      | `C:\w\my-app`                | `C:\w\my\app` | **失敗**                         |
| T2  | 來源／目標同一 session 目錄（大小寫、結尾斜線不同）               | 判為同一個                   | 判為同一個    | 通過                             |

### 1.3 RewriteHeaderCwd

| ID  | 輸入                                             | 預期                             | 實際                      | 通過             |
| --- | ------------------------------------------------ | -------------------------------- | ------------------------- | ---------------- |
| R1  | 一般 header，新 cwd=`D:\new\dir`                 | 改寫、JSON 合法、cwd 正確        | 相同                      | 通過             |
| R2  | 行尾 `\r\n`                                      | 改寫、`\r\n` 保留、JSON 合法     | 相同                      | 通過             |
| R3  | 新 cwd 含中文、引號、反斜線                      | 跳脫正確、解析後與輸入相同       | 相同                      | 通過             |
| R4  | cwd 已是目標                                     | changed=False，note=已是目標     | 相同                      | 通過             |
| R5  | header 沒有 cwd（CRLF）                          | 在最後一個 `}` 前插入，JSON 合法 | 相同                      | 通過             |
| R5b | header 被截斷、沒有 `}`                          | changed=False + 說明             | 相同                      | 通過             |
| R6  | header 在第 3 行                                 | 改寫                             | 改寫                      | 通過             |
| R7  | header 在第 205 行（超過 HeaderScanLines=200）   | 找不到                           | 找不到                    | 通過（行為一致） |
| R8  | 字串開頭帶 `\uFEFF`                              | 仍能改寫                         | changed=True              | 通過             |
| R9  | header 較前面的字串值裡含假的 `"cwd":"zzz"` 字樣 | 改到真正的 cwd 欄位              | 改到真正的 cwd，JSON 合法 | 通過             |
| R10 | 新 cwd 含 `$1`、`$&`                             | 原樣寫入                         | 原樣寫入                  | 通過             |
| R11 | 只有 message 行、沒有 session header             | 回報找不到                       | 回報找不到                | 通過             |

### 1.4 ReadHeader 與 MoveOne 的一致性

| ID  | 輸入                                                              | 預期     | 實際                                                   | 通過     |
| --- | ----------------------------------------------------------------- | -------- | ------------------------------------------------------ | -------- |
| B1  | 10 行各 100KB 的 message，之後才是 header（第 11 行，位置約 1MB） | 搬移成功 | 回傳「寫入後驗證失敗」；來源保留，目標已寫入，兩邊都有 | **失敗** |
| B2  | header 單行 600KB，cwd 欄位在 512K 字元之後                       | 搬移成功 | 同 B1                                                  | **失敗** |

### 1.5 MoveOne

| ID  | 輸入                                               | 預期                           | 實際                                           | 通過                                                          |
| --- | -------------------------------------------------- | ------------------------------ | ---------------------------------------------- | ------------------------------------------------------------- |
| M1  | 來源有 BOM + CRLF                                  | 內容保留                       | BOM 被移除；CRLF 保留；來源已刪                | 部分通過（BOM 遺失）                                          |
| M2  | 檔內含非 UTF-8 位元組 `0xE9`                       | 原樣保留                       | 被換成替代字元，位元組不見了                   | **失敗**                                                      |
| M2b | 同 M2，但 cwd 已是目標（本來不用改）               | 原樣保留                       | 同樣被破壞（`changed=false` 也會重新編碼寫入） | **失敗**                                                      |
| M3  | 目標檔已存在（內容 `PRECIOUS`）                    | 拒絕或保留                     | 被靜默覆蓋，回傳成功                           | **失敗**                                                      |
| M4  | 來源被獨占鎖定（FileShare.None）                   | 讀取失敗、目標不產生           | 「讀取失敗」，目標不產生                       | 通過                                                          |
| M5  | 來源被別的程序以可讀寫共用開著（如 pi 正在寫）     | 拒絕搬移                       | 「讀取失敗」，來源與目標都沒動                 | 通過（剛好安全，因為 `File.ReadAllText` 使用 FileShare.Read） |
| M5b | 來源以 Read／FileShare.Read 開著（讀得到、刪不掉） | 提示兩邊都有                   | 回傳訊息；來源與目標都存在                     | 通過                                                          |
| M6  | 損壞檔（沒有 header）                              | 回傳說明、來源保留             | 相同                                           | 通過                                                          |
| M7  | 新 cwd 含非法路徑字元（`C:\a                       | b`）                           | 驗證應該擋下                                   | 回傳成功（驗證比的是兩個空字串）                              | **失敗**（低） |
| M8  | 空檔                                               | 回傳說明                       | 「找不到 session header」                      | 通過                                                          |
| M9b | 目標資料夾不存在                                   | 回傳錯誤、來源保留、不留暫存檔 | 相同                                           | 通過                                                          |
| M10 | 目標是唯讀檔                                       | 回傳錯誤、目標保留             | 回傳錯誤、目標保留                             | 通過                                                          |

### 1.6 CollectFiles

| ID  | 輸入                                                                        | 預期             | 實際                                        | 通過 |
| --- | --------------------------------------------------------------------------- | ---------------- | ------------------------------------------- | ---- |
| K1  | 資料夾內有 `.tmp-1-1`、`a.jsonl.tmp`、`.tmp-9.jsonl`、`U.JSONL`、`u2.jsonl` | 不含 .tmp 相關檔 | 只回傳 `U.JSONL` 與 `u2.jsonl` 與其他正常檔 | 通過 |
| K2  | `z.jsonl2` 是否被 `*.jsonl` 比到                                            | 否               | 否                                          | 通過 |

大小寫不同的同名檔：在一般（不分大小寫）的 NTFS 資料夾裡無法建立，所以這個情境沒實測。`CollectFiles` 的 `seen` HashSet 用來處理這個情況，在一般 Windows 上不會有作用（見冗餘 R-4）。
衝突偵測 `File.Exists(dest)` 在 Windows 上不分大小寫，和實際檔案系統一致。

### 1.7 效能實測

| 項目                                                        | 結果                                                                                          |
| ----------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| `MoveOne` 97MB 單檔                                         | 818 ms，期間 15 次 gen2 GC（整檔讀成字串、Split、Join、再編碼寫出，記憶體約為檔案大小的數倍） |
| `FindSessionDirs` 找不到時，300 個資料夾 × 10 個 50KB jsonl | 856 ms；gen0=485、gen2=480 次 GC（每次 `ReadHeader` 都配置 1MB 的 `char[]`，進 LOH）          |
| `ReadHeader` 連續 300 次讀 50KB 檔                          | 66 ms                                                                                         |
| `WrapPath` 250 字元、寬 300px                               | 12.3 ms／次                                                                                   |
| `WrapPath` 45 字元、寬 500px                                | 2.4 ms／次                                                                                    |

## 2. 真實 Bug 清單

### B-1（高）空 session 資料夾的解碼有損，錯誤 cwd 會被寫進 jsonl 檔頭

- 重現：測試資料夾有空的 `--C--w-my-app--`，把它當目標拖入。`ClassifyDrop` 回傳 cwd=`C:\w\my\app`（T1b、C3）。
- 影響：`FindTargetDir` 重新編碼後剛好指向同一個資料夾（T1、C14b），所以目錄沒錯。但 `TryConvert` 把 `newCwd = _targetCwd` 寫進所有搬過去的檔頭，變成不存在的 `C:\w\my\app`。pi 之後是靠檔頭 cwd 顯示／還原工作目錄，使用者看到的對話所屬路徑是錯的。
- 碰到的條件：目標 session 資料夾是空的（沒有 jsonl 可讀 cwd），而且專案路徑中有 `-`（my-app、zen-asr 之類很常見）。有 jsonl 時會優先用檔頭 cwd，不受影響（C3b）。
- 建議修法：解碼時用檔案系統驗證。對含 `-` 的片段逐一嘗試「`-` 當 `\`」或「保留 `-`」的組合，取第一個 `Directory.Exists` 成立的結果；都不存在才退回全部當 `\`，並在確認視窗警告「cwd 由目錄名推測，可能不準」。

```csharp
// 由左到右回溯：每個 '-' 可能是分隔符，也可能是名稱的一部分
public static string DecodeSessionDirName(string name)
{
    if (!LooksLikeSessionDirName(name)) return null;
    string inner = name.Substring(2, name.Length - 4);
    Match m = Regex.Match(inner, @"^([A-Za-z])--(.+)$");
    if (!m.Success) return null;
    string drive = m.Groups[1].Value.ToUpperInvariant() + ":\\";
    string found = ResolveByFs(drive, m.Groups[2].Value.Split('-'), 0);
    return found ?? (drive + m.Groups[2].Value.Replace('-', '\\'));   // 最後退路（有損）
}

private static string ResolveByFs(string cur, string[] parts, int i)
{
    if (i == parts.Length) return Directory.Exists(cur) ? cur : null;
    string seg = parts[i];
    for (int j = i; j < parts.Length; j++)
    {
        if (j > i) seg += "-" + parts[j];
        string next = Path.Combine(cur, seg);
        if (!Directory.Exists(next)) continue;
        string r = ResolveByFs(next, parts, j + 1);
        if (r != null) return r;
    }
    return null;
}
```

註：空片段（`my--app` 造成的空字串）要另外處理，見 B-2。此寫法在專案資料夾已被刪除或改名時仍會退回有損結果，所以需要那則警告。

### B-2（低）名稱含連續 `-` 時，解碼得到含雙反斜線的路徑

- 重現：`DecodeSessionDirName("--C--x-my--app--")` 回傳 `C:\x\my\\app`（C14d）。
- 影響：路徑不合法。`PathKey`、`Path.GetFullPath` 會自動把它正規化成 `C:\x\my\app`，實際不會崩潰，但內容仍然錯。
- 建議修法：併入 B-1 的檔案系統回溯。至少 `Replace('-', '\\')` 之後 `Regex.Replace(path, @"\\{2,}", @"\")`，再標示為推測。

### B-3（中）`ClassifyDrop` 只看名稱，會把一般專案資料夾誤判成 session 資料夾

- 重現：拖入 `<T>\proj\--notsession--`（C10）：回傳 isSource=True、cwd=null，GUI 會跳出「無法從這個 session 資料夾解析 cwd」，這個專案根本沒辦法當來源／目標。拖入 `<T>\proj\--C--real--`（C10b）：被還原成 `C:\real`，與實際路徑無關。
- 原因：`LooksLikeSessionDirName` 只檢查前後兩個 `--`，`sessionsRoot` 參數宣告了卻沒有使用。
- 建議修法：加上位置條件，只有「父資料夾就是 sessions 根目錄」才視為 session 資料夾。

```csharp
string parent = Path.GetDirectoryName(full.TrimEnd('\\', '/'));
bool underRoot = !string.IsNullOrEmpty(sessionsRoot)
    && PathKey(parent) == PathKey(sessionsRoot);
if (underRoot && LooksLikeSessionDirName(name)) { ... }
```

取捨：使用者若把 sessions 根目錄設在別處（config 的 `sessions`）而拖入另一處備份的 session 資料夾，就不會被辨認成 session。可以改成「父資料夾等於 sessions 根目錄，或資料夾內有 jsonl 且 header 是 `type:session`」，兩個條件任一成立就算。

### B-4（中）拖入的 session 資料夾與其 jsonl cwd 對不上時，檔案會進到另一個資料夾

- 重現：`--C--old-name--` 的 jsonl 檔頭 cwd 是 `C:\other\place`。當目標拖入後，`FindTargetDir` 以 cwd 重新編碼，得到 `--C--other-place--`（C12b）。使用者以為搬進他拖入的資料夾，實際上會新建另一個資料夾，確認視窗只顯示 cwd 和來源目錄名，沒提到目標資料夾名稱。
- 建議修法：`ClassifyDrop` 已經有 `sessionDir`。`AssignTargetFromPath` 應該保存它（例如 `_targetDir`），`TryConvert` 在該資料夾仍存在時直接用它，不要重新編碼。另外在確認視窗加一行「目標目錄：`<資料夾名>`」。

```csharp
// AssignTargetFromPath
_targetDir = isSession ? sessionDir : null;
// TryConvert
string tgtDir = (!string.IsNullOrEmpty(_targetDir) && Directory.Exists(_targetDir))
    ? _targetDir
    : PiPaths.FindTargetDir(sessionsRoot, _targetCwd);
// ClearTarget 要一起清掉 _targetDir；msg 加上 "目標目錄：" + Path.GetFileName(tgtDir)
```

### B-5（中）`MoveOne` 會靜默覆蓋已存在的目標檔

- 重現：目標已有 `ex.jsonl`（內容 `PRECIOUS`），直接呼叫 `MoveOne`，回傳 null（成功），目標被覆蓋（M3）。
- GUI 在 `TryConvert` 已經先檢查 `File.Exists(dest)`，所以正常流程會被擋下。但檢查到真正寫入之間有時間差，而且 `MoveOne` 本身沒有保護。
- 建議修法：在 `MoveOne` 開頭檢查，存在就回傳訊息，不覆蓋。

```csharp
if (File.Exists(dst)) return "目標已存在同名檔，略過：" + Path.GetFileName(dst);
```

### B-6（中）非 UTF-8 的位元組會被破壞，`changed=false` 也一樣

- 重現：檔案中有單獨的 `0xE9`（M2）。`File.ReadAllText(..., UTF8)` 把它換成 U+FFFD，再寫出時成為 `EF BF BD`。即使 cwd 已經是目標、不需要改寫（M2b），仍然會重新編碼寫出。
- 現實風險：pi 寫的 jsonl 是 UTF-8，正常檔案不會碰到。但只要檔案被外部工具動過或截斷在多位元組字元中間，就會悄悄損毀，而來源隨後被刪除，沒有回頭路。
- 建議修法：（甲）只處理第一行 header，其餘位元組原樣複製，見效能 P-3，這一併解決 B-6、BOM 遺失、記憶體問題。（乙）最小修法：改寫前用嚴格 UTF-8 解碼，失敗就不搬。

```csharp
UTF8Encoding strict = new UTF8Encoding(false, true);   // throwOnInvalidBytes
try { original = strict.GetString(File.ReadAllBytes(src)); }
catch (Exception ex) { return "讀取失敗或不是合法 UTF-8：" + ex.Message; }
```

（乙）會讓 BOM 進入字串，需另外用 `original[0] == '\uFEFF'` 判斷並在寫出時保留。

### B-7（中）header 超過 512K 字元時，寫入成功卻驗證失敗，留下兩份檔案

- 重現：header 在第 11 行、前面有約 1MB 的其他行（B1），或 header 單行 600KB（B2）。`RewriteHeaderCwd` 看得到 header（只限制行數 200，不限位元組），寫入成功；之後 `ReadHeader(dst)` 只讀前 512K 字元，找不到，回報「寫入後驗證失敗」。來源保留、目標已寫入。
- 影響：使用者看到失敗，但其實已經有一份改寫過的副本在目標資料夾。重試時會因為「目標已有同名檔」被擋下。
- 真實 pi 檔案的 header 在第一行且很短，所以只有異常檔案才會碰到，優先度中低。
- 建議修法：`MoveOne` 驗證改用記憶體中已有的 `newText`，不必重新讀檔；或 `ReadHeader` 與 `RewriteHeaderCwd` 使用同一套掃描範圍。

```csharp
// 寫入後用新文字驗證（同時省掉一次 512KB 讀取）
Dictionary<string, object> header = HeaderFromText(changed ? newText : original);
```

### B-8（低）PathKey 對無效路徑回傳空字串，兩個無效路徑會被當成相等

- 重現：`PathKey("") == PathKey("C:\a|b")` 為 True（S4）。`MoveOne` 的寫入後驗證因此對非法字元的 cwd 永遠通過（M7）。
- 影響：`TryConvert` 前已經有 `NormalizeCwd` 擋掉大部分非法路徑，實際很難碰到。
- 建議修法：`PathKey` 失敗時回傳 `null`，比較處用 `string.Equals` 且雙方非 null；或失敗時回傳原字串的小寫版本。

### B-9（中）`RunMove` 沒有 try/finally，意外例外會讓 UI 永遠停在「搬移中」（讀碼推論，未實測）

- `_working = true` 之後若 `MoveOne` 以外的程式丟出例外（例如 `RewriteHeaderCwd` 對很大的檔案做 `string.Join` 時 OutOfMemoryException，`MoveOne` 內只有讀檔和寫檔有 try/catch），例外會傳到 WinForms 的未處理例外對話框，`_working` 保持 true。之後 `TryConvert` 第一行直接 return、`OnSurfaceClick` 也直接 return，使用者必須重開程式。
- 建議修法：

```csharp
_working = true;
try { /* 原本整段搬移流程 */ }
finally { _working = false; SyncPathRows(); RefreshHint(); }
```

### B-10（低）拖放處理程序內直接彈出對話框與長時間搬移（讀碼推論，未實測）

- `OnDragDrop → ApplyDropToNextSlot → TryConvert` 在 OLE 拖放回呼內同步執行 MessageBox 與整個 `RunMove`。這段時間拖曳來源（檔案總管）會被卡住，大檔案搬移時視窗也沒有回應。
- `initialDrop` 已經用 `BeginInvoke` 延後執行，拖放也應該比照。

```csharp
private void OnDragDrop(object sender, DragEventArgs e)
{
    ...
    string d = dir;
    BeginInvoke(new Action(delegate { ApplyDropToNextSlot(d, true); }));
}
```

## 3. 冗餘程式碼

| 編號 | 位置                                                                                                                | 說明                                                                                                                                                                                                                                      | 嚴重度 | 建議                                                                                                                                                          |
| ---- | ------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| R-1  | `ClassifyDrop` 的 `sessionsRoot` 參數                                                                               | 宣告了兩個呼叫端都有傳，函式內完全沒用                                                                                                                                                                                                    | 低     | 依 B-3 讓它派上用場；若不採用就移除參數                                                                                                                       |
| R-2  | `DecodeSessionDirName` 與 `EncodeSessionDirName` 不對稱                                                             | Encode 把 `\ / :` 都換成 `-`，Decode 只能把 `-` 一律當 `\`；UNC、磁碟機根目錄、含 `-` 的名稱都無法還原（C11d、C11e、C14）                                                                                                                 | 中     | 見 B-1；文件註解要寫明「有損」                                                                                                                                |
| R-3  | `ReadHeader` 與 `RewriteHeaderCwd` 各自掃描 header、各自解析                                                        | 都是「取前 200 行→找 `type:session`」，`TryParseLooseJson` 之後 `RewriteHeaderCwd` 又用 `CwdRe` 對同一行再比對一次                                                                                                                        | 低     | 抽出一個共用函式 `FindHeaderLine(string[] lines)` 回傳行號，兩邊共用                                                                                          |
| R-4  | `CollectFiles` 的 `seen` HashSet 與 `.tmp` 過濾                                                                     | NTFS 一般資料夾不會有大小寫不同的同名檔；`Directory.GetFiles("*.jsonl")` 本來就不會回傳 `.tmp-*`、`*.jsonl.tmp`（K1、K2）。`.tmp-9.jsonl` 這種理論上才會被 StartsWith 過濾                                                                | 低     | 刪掉 `seen` 與 `EndsWith(".tmp")`；`.tmp-` 前綴判斷可保留，因為 `AtomicWriteText` 的暫存檔名是 `.tmp-PID-seq`（沒有 .jsonl 副檔名），其實也比不到，整段都可刪 |
| R-5  | 每次都重新建立的 Regex                                                                                              | `TryParseLooseJson` 兩個 `Regex.Match(字串樣式)`、`EncodeSessionDirName` 兩個 `Regex.Replace`、`DecodeSessionDirName` 一個，都走靜態 Regex 快取，不是每次重編，但仍有快取查詢與樣式字串比對成本；`Config.UpsertStatic` 則每次 `new Regex` | 低     | 改成 `private static readonly Regex`                                                                                                                          |
| R-6  | `TryParseLooseJson` 外層 try/catch                                                                                  | Regex 比對與 `UnescapeJson` 不會丟例外                                                                                                                                                                                                    | 低     | 移除 try/catch                                                                                                                                                |
| R-7  | `FindSessionDirs` 第三段與 `DirExampleCwd`                                                                          | 都是「列出資料夾的 jsonl、排序、逐檔 ReadHeader」                                                                                                                                                                                         | 低     | 抽出共用的 `EnumerateHeaders(dir)`                                                                                                                            |
| R-8  | `FindSessionDirs` 與 `FindTargetDir` 都呼叫 `ListSessionDirs`；`TryConvert` 先 `FindSessionDirs` 再 `FindTargetDir` | 同一個資料夾列舉做兩次，`ToLowerInvariant` 在迴圈內重複                                                                                                                                                                                   | 低     | 列舉一次後傳入；比較用 `StringComparison.OrdinalIgnoreCase`，不要先轉小寫                                                                                     |
| R-9  | `ProcessId()` 每次 `Process.GetCurrentProcess()`                                                                    | 每個檔案搬移都建立一個 Process 物件（沒有 Dispose）                                                                                                                                                                                       | 低     | 改成 `private static readonly int Pid = Process.GetCurrentProcess().Id;` 或 `Environment.ProcessId` 不可用（C# 5／.NET 4）就用靜態欄位                        |
| R-10 | `AtomicWriteText` 的 `finally` 刪暫存檔                                                                             | 成功時 Move 之後暫存檔已不存在，`File.Exists` 檢查是多餘的；但失敗時需要它，所以保留價值在失敗路徑                                                                                                                                        | 低     | 可保留；若要精簡，只在 catch 中刪除                                                                                                                           |
| R-11 | `ClassifyDrop` 的 `isSource=false` 重複設定                                                                         | 開頭已初始化，尾端又設一次                                                                                                                                                                                                                | 低     | 刪除尾端那行                                                                                                                                                  |
| R-12 | `Config.Load` 讀設定檔兩次                                                                                          | 第一次讀內容解析，缺欄位時再讀一次補寫                                                                                                                                                                                                    | 低     | 重用第一次讀到的 `raw`                                                                                                                                        |

## 4. 效能問題

### P-1（中）`ReadHeader` 每次配置 512K 字元（1MB）的緩衝區，再對整段做 `Split('\n')`

- 實測：連續讀 300 次 50KB 檔 66 ms；`FindSessionDirs` 無命中時 300 個資料夾×10 檔 856 ms，gen2 GC 480 次（1MB 的 `char[]` 直接進 LOH）。
- 問題有兩個：緩衝區遠大於實際需要；`Split` 會把整段切成所有行，實際只用前 200 行。
- 建議修法：用 `StreamReader.ReadLine()` 最多讀 200 行，遇到 header 立即停止，不配置大緩衝。

```csharp
using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true, 4096))
{
    string line;
    long chars = 0;
    for (int i = 0; i < HeaderScanLines && chars < HeaderScanBytes
                    && (line = sr.ReadLine()) != null; i++)
    {
        chars += line.Length + 1;
        string s = line.Trim();
        if (!s.StartsWith("{")) continue;
        Dictionary<string, object> obj = TryParseLooseJson(s);
        if (obj == null) continue;
        object type;
        if (obj.TryGetValue("type", out type) && type != null && type.ToString() == "session")
            return obj;
    }
}
return null;
```

註：如果採用，`ReadLine` 遇到超長單行（B2）仍會把整行讀進記憶體。可以再加一個單行長度上限，超過就略過該行，並讓 `MoveOne` 與 `RewriteHeaderCwd` 用同一個上限（連帶解決 B-7）。

### P-2（中）`FindSessionDirs` 第三段：找不到時會讀遍所有資料夾的所有 jsonl 檔頭

- 第三段對每個資料夾的每個 jsonl 都呼叫 `ReadHeader`，只有「命中」才 `break`。沒命中時，所有對話檔的 header 都會被讀一次（實測 3000 個檔 856 ms）。真實使用者可能有上千個 session，而一個資料夾裡所有檔案的 cwd 通常相同。
- 建議修法：每個資料夾只看第一個有 header 的檔案（也就是改用 `DirExampleCwd`）。另外第三段只在前兩段都找不到時才會跑，可以在 UI 顯示「搜尋中…」或至少先 `Cursor = WaitCursor`。

```csharp
foreach (string d in dirs)
{
    string c = DirExampleCwd(d);                       // 只讀到第一個有 cwd 的檔案
    if (c != null && PathKey(c) == key) hits.Add(d);
}
```

取捨：同一資料夾內若混有不同 cwd 的檔案（例如使用者手動搬過檔案），只看第一個檔會漏掉後面檔案的 cwd。可以保守地只在「資料夾內所有檔案 cwd 相同」的常見情況下加速；或維持現狀但只在沒命中時才做全掃描（現況）。

### P-3（中）`MoveOne` 整檔讀入記憶體，再 `Split`、`Join`、重新編碼

- 實測：97MB 單檔 818 ms，15 次 gen2 GC。記憶體峰值大約是檔案大小的 4 到 5 倍（原始字串、Split 的行陣列、Join 後字串、編碼後位元組）。對上百 MB 的長對話，可能出現 OutOfMemoryException，而該例外在 `RewriteHeaderCwd` 內不會被攔住（見 B-9）。
- 建議修法（同時解決 B-6、BOM）：只讀改 header 所在的那一行，其餘用位元組串流複製。

```csharp
// 1) 以位元組讀取開頭最多 N KB，找到第一行 header 的位元組範圍
// 2) 只對那一行做 UTF-8 解碼→RewriteHeaderCwd→編碼
// 3) 寫入：新 header 行 + 來源串流其餘位元組（CopyTo）
using (FileStream fin = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
using (FileStream fout = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
{
    fout.Write(prefixBytes, 0, prefixBytes.Length);   // header 之前的位元組（含 BOM）
    byte[] newHeader = new UTF8Encoding(false).GetBytes(newHeaderLine);
    fout.Write(newHeader, 0, newHeader.Length);
    fin.Position = headerEndOffset;
    fin.CopyTo(fout);
    fout.Flush(true);
}
```

這樣大檔案也只佔固定記憶體，不會重新編碼對話內容。

### P-4（低）寫入後再讀一次目標 header

- `MoveOne` 寫完後用 `ReadHeader(dst)` 重新讀取並驗證，多一次 512KB 讀取。若採用 B-7 的建議（直接用 `newText` 驗證），這一步可省；想保留「確認落盤內容」的保險，則讀取範圍只需要第一個 4KB。

### P-5（低）`AtomicWriteText`：先 `File.Delete` 再 `File.Move`，不是真正的原子操作

- 若 Delete 成功、Move 失敗（磁碟滿、防毒軟體攔截、權限），目標檔就不見了。由於 B-5 的建議會讓「目標已存在」的情況不再發生，這個視窗實際上只會在正常流程中「目標不存在」時出現，影響很小。
- 若日後仍需要覆寫能力，使用 `File.Replace`（.NET 4 可用），它在同一磁碟區上是原子替換。

```csharp
if (File.Exists(path)) File.Replace(tmp, path, null);
else File.Move(tmp, path);
```

### P-6（低）`WrapPath` 逐字元 `MeasureText`，複雜度 O(n²)，而且會多算用不到的行

- 實測：250 字元、寬 300px 每次 12.3 ms；45 字元 2.4 ms。`LayoutCard` 在每次視窗大小改變（`Resize`）和 `SyncPathRows` 時都會對兩列各跑一次，拖曳縮放視窗時每個事件約 20 到 30 ms，可感覺到遲滯但不嚴重。
- 另外 `LayoutRow` 只顯示前 3 行，`WrapPath` 卻會把整條路徑全部換行，後面再丟棄。
- 建議修法：（甲）換行到 `MaxPathLines + 1` 行就停止；（乙）用二分搜尋找出每行能放的字元數，把量測次數從 O(n²) 降到 O(n log n)；（丙）快取 `(text, width)` 對應的結果，寬度沒變就不重算。

```csharp
// (乙) 二分搜尋
int lo = 1, hi = text.Length - start, best = 1;
while (lo <= hi)
{
    int mid = (lo + hi) / 2;
    int w = TextRenderer.MeasureText(g, text.Substring(start, mid), font,
        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Width;
    if (w <= maxWidth) { best = mid; lo = mid + 1; } else hi = mid - 1;
}
```

## 5. 設計與資料面的取捨觀察（未列為 bug）

- M5：來源被 pi 以可讀寫共用開著時，`File.ReadAllText` 會因共用衝突失敗，因此搬移被拒絕。這剛好對使用中的 session 是安全的，但這是副作用，不是明確設計。若日後把讀取改成 `FileShare.ReadWrite`（例如為了 P-3 的串流讀），就會變成可以搬「正在寫入」的檔案，並在刪除來源時失敗或丟失最後幾行。改串流時請保留 `FileShare.Read`。
- M1：BOM 會被移除。pi 自己寫的 jsonl 沒有 BOM，所以影響很小。
- C7：`ClassifyDrop` 對非 session 資料夾回傳的 cwd 沒有正規化，但目前兩個呼叫端都不使用它。日後如果有人改成使用，就會把結尾斜線帶進去。建議讓它回傳 `NormalizeCwd(full)`。
- C11d／C11e：磁碟機根目錄與 UNC 路徑的「空」session 資料夾無法解碼，只能靠 jsonl；沒有 jsonl 時 GUI 會提示無法解析，這是合理的保守行為。

## 6. GUI 流程分析（讀碼，未實測）

1. 拖入順序：第一次拖入一律設為來源（`ApplyDropToNextSlot`），第二次設為目標並嘗試轉換，之後再拖入都是覆寫目標並再次嘗試轉換。來源要更換必須先按「清空」，這是設計行為，提示文字已經寫明。
2. `ClearSource` 會同時把 `_sourceCwd` 與 `_sourceDir` 設為 null；`AssignSourceFromPath` 的非 session 分支也會重設 `_sourceDir = null`。沒有發現殘留。
3. 搬移成功後 `_sourceCwd`、`_targetCwd` 都保留。來源資料夾被清空刪除時 `_sourceDir` 會設為 null，但 `_sourceCwd` 仍在。如果使用者接著再拖一個新目標，`TryConvert` 會經由 `FindSessionDirs` 重新找來源，找不到就彈出「找不到來源對應的對話目錄」。訊息正確，只是對使用者有點突兀。建議搬移完成後自動清空來源（或兩邊都清空）。
4. 來源是 session 資料夾、但 `_sourceDir` 已被刪除：`TryConvert` 會跳過 `_sourceDir`，改用 `_sourceCwd` 編碼後在 sessions 根目錄裡找。若 `_sourceCwd` 來自 jsonl（與目錄名不一致），`FindSessionDirs` 第三段會靠檔頭找回；找不到就顯示找不到。行為合理。
5. `RunMove` 中途失敗：檔案是逐個搬的，沒有整批回復。失敗的檔案留在來源，成功的已在目標；全部失敗且目標目錄是這次新建的，會把空目錄刪掉。遇到 B-7 的情況時則是目標與來源各有一份，需要手動處理，見 B-7。例外沒有被攔住的風險見 B-9。
6. `Application.DoEvents` 重入風險：只在 `RunMove` 開頭呼叫一次，之後整個迴圈是同步執行，UI 不會處理事件。DoEvents 那一次可能先處理已排隊的拖放訊息，而 `AssignTargetFromPath` 沒有檢查 `_working`，會改掉 `_targetCwd`；`RunMove` 的參數已經先取好所以搬移本身不受影響，但完成後 `SyncPathRows` 會顯示新的目標，與實際搬去的位置不一致。風險低；建議 `ApplyDropToNextSlot` 開頭加 `if (_working) return;`。
7. `PickFolderForSource` 預設開在 sessions 根目錄：使用者選到裡面的 session 資料夾，經過 `AssignSourceFromPath → ClassifyDrop`，和拖入相同，會被正確辨認。若使用者直接選 sessions 根目錄本身，會被當成一般專案路徑，`FindSessionDirs` 找不到，顯示「找不到來源對應的對話目錄」；當目標選到的話，則會編碼成 `--C--Users-artin-.pi-agent-sessions--`，技術上合法但多半不是使用者想要的。建議在 `AssignSourceFromPath`／`AssignTargetFromPath` 遇到路徑等於 sessions 根目錄時直接提示。
8. B-3 和 B-4 也會影響 `PickFolderFor*`：它們都走同一條 `ClassifyDrop` 路徑。

## 7. 建議修補順序

1. **B-1 + B-2**（高）：解碼時用檔案系統驗證；無法驗證時在確認視窗警告 cwd 是推測的。這是這次修補留下的主要缺口。
2. **B-4**（中）：保存拖入的 session 資料夾當 `_targetDir`，確認視窗加「目標目錄」。與 B-1 同在 `AssignTargetFromPath`／`TryConvert`，可以一起改。
3. **B-3**（中）：用 `sessionsRoot` 或 jsonl header 判斷是否真的是 session 資料夾，順便消除 R-1。
4. **B-5**（中）：`MoveOne` 開頭檢查目標已存在，一行即可。
5. **B-9**（中）：`RunMove` 加 try/finally，順便在 `ApplyDropToNextSlot` 加 `_working` 檢查。
6. **P-3 + B-6 + B-7 + P-4**（中）：把 `MoveOne` 改成只改寫 header 行、其餘位元組串流複製，驗證使用記憶體中的新 header。這一組一起做最划算，一次解掉非 UTF-8 損毀、BOM 遺失、OOM、512K 驗證不一致。改動最大，建議單獨一個 commit 並重跑本報告的 M 系列測試。
7. **P-1 + P-2**（中）：`ReadHeader` 改 `ReadLine`、`FindSessionDirs` 第三段每資料夾只讀一個檔。
8. **B-10**（低）：拖放回呼內改用 `BeginInvoke`。
9. **B-8、P-5、P-6、R-1 到 R-12**（低）：清理與小幅優化，有空再做。
