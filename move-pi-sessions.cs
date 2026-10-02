// ==========================================================================
//  move-pi-sessions.cs — 批次搬移 pi 對話（session jsonl）到另一個專案，
//                        並同步改寫檔頭 cwd。
//
//  編譯：把本檔拖到 csc-build.cmd 即可（不需要另外的 build 腳本）。
//        圖示由本檔 AppIcon 以程式碼繪製；csc-build.cmd 偵測到 "--make-ico" 後，
//        會先編暫存 exe 產生暫存 .ico、再嵌入正式 exe，完成後自動刪除。
//
//  注意：Windows 內建 csc.exe 是 C# 5，不使用 $"..."、?.、nameof 等語法。
//
//  操作：
//    · 拖入資料夾 / 點擊空白區：第一次設來源、第二次設目標（再拖則覆寫目標）
//    · 原始／目標路徑列上的 [選擇] / [清空] 可個別選路徑或清除
//    · 右上角齒輪 → 檔案總管開啟並選取 move-pi-sessions-config.ini
//    · 兩邊路徑都就緒時，轉換前會跳出確認
//
//  move-pi-sessions-config.ini 只存 pi sessions 路徑；留空 = %USERPROFILE%\.pi\agent\sessions
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace MovePiSessions
{
    // ---------------------------------------------------------------- 進入點
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            // 編譯輔助：move-pi-sessions.exe --make-ico out.ico（csc-build.cmd 用來產生 exe 圖示）
            if (args != null && args.Length == 2 && args[0] == "--make-ico")
            {
                AppIcon.WriteIco(args[1]);
                return;
            }

            try { SetProcessDPIAware(); }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 拖到 exe 圖示：命令列會帶路徑
            string dropPath = null;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (a == null) continue;
                    string t = a.Trim().Trim('"');
                    if (t.Length == 0) continue;
                    if (t.StartsWith("-") || t.StartsWith("/")) continue;
                    if (Directory.Exists(t))
                    {
                        dropPath = Path.GetFullPath(t);
                        break;
                    }
                }
            }

            using (MainForm form = new MainForm(dropPath))
            {
                Application.Run(form);
            }
        }
    }

    // --------------------------------------------------------------- 設定檔
    internal sealed class Config
    {
        public const string FileName = "move-pi-sessions-config.ini";

        public string FilePath = "";
        // 留空 = 使用預設 %USERPROFILE%\.pi\agent\sessions
        public string Sessions = "";
        public string UiFont = "Microsoft YaHei UI";
        public float UiFontSize = 11f;
        public bool CreatedNew = false;

        private const string Template =
@"; pi 對話搬移工具設定檔
; 格式：key = value　　以 ; 或 # 開頭的整行是註解
; 路徑直接寫就好，不需要把反斜線重複

; pi sessions 目錄（留空 = %USERPROFILE%\.pi\agent\sessions）
sessions =

; 介面字型與大小（系統已安裝的字型；大小建議 9～24）
uiFont = Microsoft YaHei UI
uiFontSize = 11
";

        public static Config Load(string exeDir)
        {
            Config c = new Config();
            try { c.FilePath = Path.Combine(exeDir, FileName); }
            catch { c.FilePath = FileName; }

            if (!File.Exists(c.FilePath))
            {
                try
                {
                    File.WriteAllText(c.FilePath, Template, new UTF8Encoding(true));
                    c.CreatedNew = true;
                }
                catch { }
                return c;
            }

            string raw;
            try { raw = File.ReadAllText(c.FilePath, Encoding.UTF8); }
            catch { return c; }

            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);
            foreach (string line0 in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = line0.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);
                d[key] = val;
            }

            string v;
            if (d.TryGetValue("sessions", out v)) c.Sessions = v ?? "";
            if (d.TryGetValue("uiFont", out v) && !string.IsNullOrEmpty(v)) c.UiFont = v.Trim();
            if (d.TryGetValue("uiFontSize", out v))
            {
                float f;
                if (float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out f)
                    && f >= 8f && f <= 36f)
                    c.UiFontSize = f;
            }

            // 舊版設定檔沒有字型欄位時補上
            if (!d.ContainsKey("uiFont") || !d.ContainsKey("uiFontSize"))
            {
                try
                {
                    string text = File.ReadAllText(c.FilePath, Encoding.UTF8);
                    if (!d.ContainsKey("uiFont"))
                        text = UpsertStatic(text, "uiFont", c.UiFont);
                    if (!d.ContainsKey("uiFontSize"))
                        text = UpsertStatic(text, "uiFontSize",
                            c.UiFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.WriteAllText(c.FilePath, text, new UTF8Encoding(true));
                }
                catch { }
            }
            return c;
        }

        public void Save()
        {
            try
            {
                string text = File.Exists(FilePath)
                    ? File.ReadAllText(FilePath, Encoding.UTF8)
                    : Template;
                text = UpsertStatic(text, "sessions", Sessions ?? "");
                text = UpsertStatic(text, "uiFont", UiFont ?? "Microsoft YaHei UI");
                text = UpsertStatic(text, "uiFontSize",
                    UiFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, text, new UTF8Encoding(true));
            }
            catch { }
        }

        private static string UpsertStatic(string text, string key, string value)
        {
            string newLine = key + " = " + value;
            Regex re = new Regex(
                @"^[ \t]*" + Regex.Escape(key) + @"[ \t]*=.*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (re.IsMatch(text))
                return re.Replace(text, delegate(Match m) { return newLine; }, 1);
            return text.TrimEnd() + "\r\n" + newLine + "\r\n";
        }

        // 解析實際要用的 sessions 根目錄
        public string ResolveSessionsRoot()
        {
            string s = Sessions == null ? "" : Sessions.Trim();
            if (s.Length == 0)
                return PiPaths.DefaultSessionsRoot();
            s = Environment.ExpandEnvironmentVariables(s);
            try { return Path.GetFullPath(s); }
            catch { return s; }
        }

        public Font MakeUiFont(float scale, FontStyle style)
        {
            string name = string.IsNullOrEmpty(UiFont) ? "Microsoft YaHei UI" : UiFont;
            float size = UiFontSize * scale;
            if (size < 8f) size = 8f;
            try { return new Font(name, size, style); }
            catch
            {
                try { return new Font("Microsoft YaHei UI", size, style); }
                catch { return new Font(FontFamily.GenericSansSerif, size, style); }
            }
        }
    }

    // -------------------------------------- 檔案總管資料夾選取（IFileDialog）
    // CoCreateInstance + 正確 IID/vtable；錯誤 RCW 轉型會 E_NOINTERFACE。
    internal static class ExplorerFolderPicker
    {
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const uint CLSCTX_INPROC_SERVER = 1;
        private const int S_OK = 0;
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        private static readonly Guid CLSID_FileOpenDialog =
            new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        private static readonly Guid IID_IFileDialog =
            new Guid("42F85136-DB7E-439C-85F1-E4075D135FC8");
        private static readonly Guid IID_IShellItem =
            new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        public static string Pick(IWin32Window owner, string title, string initialDirectory)
        {
            IntPtr punk = IntPtr.Zero;
            IFileDialog dialog = null;
            try
            {
                Guid clsid = CLSID_FileOpenDialog;
                Guid iidDialog = IID_IFileDialog;
                int hr = CoCreateInstance(
                    ref clsid,
                    IntPtr.Zero,
                    CLSCTX_INPROC_SERVER,
                    ref iidDialog,
                    out punk);
                if (hr != S_OK || punk == IntPtr.Zero)
                    throw new COMException("CoCreateInstance(FileOpenDialog) 失敗", hr);

                dialog = (IFileDialog)Marshal.GetObjectForIUnknown(punk);
                Marshal.Release(punk);
                punk = IntPtr.Zero;

                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

                if (!string.IsNullOrEmpty(title))
                    dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    IShellItem folder = null;
                    Guid iid = IID_IShellItem;
                    int hrFolder = SHCreateItemFromParsingName(
                        initialDirectory, IntPtr.Zero, ref iid, out folder);
                    if (hrFolder == S_OK && folder != null)
                    {
                        dialog.SetFolder(folder);
                        Marshal.ReleaseComObject(folder);
                    }
                }

                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    if (owner != null) hwnd = owner.Handle;
                }
                catch { }

                hr = dialog.Show(hwnd);
                if (hr == ERROR_CANCELLED || hr != S_OK)
                    return null;

                IShellItem result;
                dialog.GetResult(out result);
                if (result == null) return null;

                IntPtr pszPath = IntPtr.Zero;
                try
                {
                    hr = result.GetDisplayName(SIGDN_FILESYSPATH, out pszPath);
                    if (hr != S_OK || pszPath == IntPtr.Zero) return null;
                    return Marshal.PtrToStringUni(pszPath);
                }
                finally
                {
                    if (pszPath != IntPtr.Zero) Marshal.FreeCoTaskMem(pszPath);
                    try { Marshal.ReleaseComObject(result); }
                    catch { }
                }
            }
            catch (COMException ex)
            {
                if (ex.ErrorCode == ERROR_CANCELLED) return null;
                throw;
            }
            finally
            {
                if (punk != IntPtr.Zero)
                {
                    try { Marshal.Release(punk); }
                    catch { }
                }
                if (dialog != null)
                {
                    try { Marshal.FinalReleaseComObject(dialog); }
                    catch { }
                }
            }
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            ref Guid riid,
            out IntPtr ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport]
        [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr hwndOwner);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            [PreserveSig]
            int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }

    // -------------------------------------- 在總管中選取路徑（比 explorer /select 快）
    internal static class ShellReveal
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        public static void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            IntPtr pidl = ILCreateFromPathW(full);
            if (pidl == IntPtr.Zero)
            {
                // 後備：仍開總管（較慢）
                try
                {
                    if (File.Exists(full))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = "/select,\"" + full + "\"",
                            UseShellExecute = true
                        });
                    }
                    else if (Directory.Exists(full))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = "\"" + full + "\"",
                            UseShellExecute = true
                        });
                    }
                }
                catch { }
                return;
            }

            try
            {
                // cidl=0：pidl 指向要選取的單一項目，會開父資料夾並選中它
                SHOpenFolderAndSelectItems(pidl, 0, null, 0);
            }
            finally
            {
                ILFree(pidl);
            }
        }
    }

    // ------------------------------------------------------------- 路徑 / session
    internal static class PiPaths
    {
        private static readonly Regex CwdRe = new Regex(
            "(\"cwd\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"",
            RegexOptions.Compiled);

        private const int HeaderScanLines = 200;
        private const int HeaderScanBytes = 512 * 1024;
        private static int _tmpSeq;

        public static string DefaultSessionsRoot()
        {
            string env = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
            string agent;
            if (!string.IsNullOrEmpty(env))
                agent = Environment.ExpandEnvironmentVariables(env);
            else
                agent = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".pi", "agent");
            return Path.Combine(agent, "sessions");
        }

        public static string PathKey(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s;
            try { s = Path.GetFullPath(p.Trim()); }
            catch { return ""; }
            s = s.ToLowerInvariant().TrimEnd('\\', '/');
            return s;
        }

        // 與 Python os.path.abspath 一致：去掉結尾斜線（磁碟機根目錄除外）
        public static string NormalizeCwd(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (full.Length > root.Length)
                full = full.TrimEnd('\\', '/');
            return full;
        }

        public static string EncodeSessionDirName(string cwd)
        {
            string resolved;
            try { resolved = NormalizeCwd(cwd); }
            catch { resolved = cwd; }
            resolved = Regex.Replace(resolved, @"^[/\\]", "");
            return "--" + Regex.Replace(resolved, @"[/\\:]", "-") + "--";
        }

        public static bool LooksLikeSessionDirName(string name)
        {
            return name != null
                && name.Length > 4
                && name.StartsWith("--")
                && name.EndsWith("--");
        }

        public static string DecodeSessionDirName(string name)
        {
            if (!LooksLikeSessionDirName(name)) return null;
            string inner = name.Substring(2, name.Length - 4);
            Match m = Regex.Match(inner, @"^([A-Za-z])--(.+)$");
            if (!m.Success) return null;
            return m.Groups[1].Value.ToUpperInvariant() + ":\\"
                + m.Groups[2].Value.Replace('-', '\\');
        }

        public static Dictionary<string, object> ReadHeader(string path)
        {
            string data;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    char[] buf = new char[HeaderScanBytes];
                    int n = sr.Read(buf, 0, buf.Length);
                    data = new string(buf, 0, n);
                }
            }
            catch { return null; }

            string[] lines = data.Split('\n');
            int limit = Math.Min(lines.Length, HeaderScanLines);
            for (int i = 0; i < limit; i++)
            {
                string s = lines[i].Trim();
                if (!s.StartsWith("{")) continue;
                Dictionary<string, object> obj = TryParseLooseJson(s);
                if (obj == null) continue;
                object type;
                if (obj.TryGetValue("type", out type)
                    && type != null
                    && string.Equals(type.ToString(), "session", StringComparison.Ordinal))
                    return obj;
            }
            return null;
        }

        private static Dictionary<string, object> TryParseLooseJson(string s)
        {
            try
            {
                Match t = Regex.Match(s, "\"type\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (!t.Success) return null;
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                d["type"] = UnescapeJson(t.Groups[1].Value);
                Match c = Regex.Match(s, "\"cwd\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (c.Success) d["cwd"] = UnescapeJson(c.Groups[1].Value);
                return d;
            }
            catch { return null; }
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                    break;
                                }
                            }
                            sb.Append('u');
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public static string EscapeJsonString(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.AppendFormat("\\u{0:x4}", (int)ch);
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string DirExampleCwd(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.jsonl");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    Dictionary<string, object> h = ReadHeader(f);
                    if (h == null) continue;
                    object cwd;
                    if (h.TryGetValue("cwd", out cwd) && cwd != null && cwd.ToString().Length > 0)
                        return cwd.ToString();
                }
            }
            catch { }
            return null;
        }

        public static List<string> ListSessionDirs(string root)
        {
            List<string> list = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                    list.Add(d);
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return list;
        }

        public static List<string> FindSessionDirs(string root, string cwd)
        {
            List<string> dirs = ListSessionDirs(root);
            string want = EncodeSessionDirName(cwd);
            List<string> exact = new List<string>();
            foreach (string d in dirs)
                if (string.Equals(Path.GetFileName(d), want, StringComparison.Ordinal))
                    exact.Add(d);
            if (exact.Count > 0) return exact;

            string low = want.ToLowerInvariant();
            List<string> ci = new List<string>();
            foreach (string d in dirs)
                if (string.Equals(Path.GetFileName(d).ToLowerInvariant(), low, StringComparison.Ordinal))
                    ci.Add(d);
            if (ci.Count > 0) return ci;

            string key = PathKey(cwd);
            List<string> hits = new List<string>();
            foreach (string d in dirs)
            {
                try
                {
                    string[] files = Directory.GetFiles(d, "*.jsonl");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        Dictionary<string, object> h = ReadHeader(f);
                        if (h == null) continue;
                        object c;
                        if (h.TryGetValue("cwd", out c) && PathKey(c == null ? "" : c.ToString()) == key)
                        {
                            hits.Add(d);
                            break;
                        }
                    }
                }
                catch { }
            }
            return hits;
        }

        public static string FindTargetDir(string root, string cwd)
        {
            string want = EncodeSessionDirName(cwd);
            string low = want.ToLowerInvariant();
            string ci = null;
            foreach (string d in ListSessionDirs(root))
            {
                string name = Path.GetFileName(d);
                if (string.Equals(name, want, StringComparison.Ordinal)) return d;
                if (ci == null && string.Equals(name.ToLowerInvariant(), low, StringComparison.Ordinal))
                    ci = d;
            }
            return ci != null ? ci : Path.Combine(root, want);
        }

        public static List<string> CollectFiles(string sourceDir)
        {
            List<string> files = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] all = Directory.GetFiles(sourceDir, "*.jsonl");
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);
                foreach (string f in all)
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(name)) files.Add(f);
                }
            }
            catch { }
            return files;
        }

        public static void RewriteHeaderCwd(string text, string newCwd,
            out string newText, out bool changed, out string note)
        {
            newText = text;
            changed = false;
            note = "";
            string[] lines = text.Split('\n');
            string jsonCwd = EscapeJsonString(newCwd);
            int limit = Math.Min(lines.Length, HeaderScanLines);
            for (int i = 0; i < limit; i++)
            {
                string line = lines[i];
                string s = line.Trim();
                if (!s.StartsWith("{") || s.IndexOf("\"type\"", StringComparison.Ordinal) < 0)
                    continue;
                Dictionary<string, object> obj = TryParseLooseJson(s);
                if (obj == null) continue;
                object type;
                if (!obj.TryGetValue("type", out type)
                    || type == null
                    || !string.Equals(type.ToString(), "session", StringComparison.Ordinal))
                    continue;

                string newLine;
                Match m = CwdRe.Match(line);
                if (m.Success)
                    newLine = line.Substring(0, m.Index) + m.Groups[1].Value + jsonCwd
                        + line.Substring(m.Index + m.Length);
                else
                {
                    int idx = line.TrimEnd().LastIndexOf('}');
                    if (idx < 0)
                    {
                        note = "session header 沒有 cwd 也無法插入";
                        return;
                    }
                    newLine = line.Substring(0, idx) + ",\"cwd\":" + jsonCwd + line.Substring(idx);
                }
                if (newLine == line)
                {
                    note = "cwd 已經是目標路徑";
                    return;
                }
                lines[i] = newLine;
                newText = string.Join("\n", lines);
                changed = true;
                return;
            }
            note = "找不到 session header（檔案可能損壞）";
        }

        public static void AtomicWriteText(string path, string text)
        {
            int seq = Interlocked.Increment(ref _tmpSeq);
            string dir = Path.GetDirectoryName(path);
            string tmp = Path.Combine(dir,
                ".tmp-" + ProcessId() + "-" + seq.ToString());
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(text);
                    sw.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { }
            }
        }

        private static int ProcessId()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        public static string MoveOne(string src, string dst, string newCwd)
        {
            string original;
            try { original = File.ReadAllText(src, Encoding.UTF8); }
            catch (Exception ex) { return "讀取失敗：" + ex.Message; }

            string newText;
            bool changed;
            string note;
            RewriteHeaderCwd(original, newCwd, out newText, out changed, out note);
            if (note.StartsWith("找不到", StringComparison.Ordinal)
                || note.StartsWith("session header 沒有", StringComparison.Ordinal))
                return note;

            try { AtomicWriteText(dst, changed ? newText : original); }
            catch (Exception ex)
            {
                return "寫入目標失敗（檔案可能正被 pi 使用）：" + ex.Message;
            }

            Dictionary<string, object> header = ReadHeader(dst);
            object cwdObj = null;
            if (header == null
                || !header.TryGetValue("cwd", out cwdObj)
                || PathKey(cwdObj == null ? "" : cwdObj.ToString()) != PathKey(newCwd))
                return "寫入後驗證失敗：" + Path.GetFileName(dst) + " 的 cwd 不是目標路徑";

            try { File.Delete(src); }
            catch (Exception ex)
            {
                return "目標已寫好但刪除來源失敗（兩邊都會出現，請手動刪來源）：" + ex.Message;
            }
            return null;
        }

        // 判斷拖進來的是 session 資料夾（來源）還是一般專案路徑（目標）
        public static void ClassifyDrop(string path, string sessionsRoot,
            out bool isSource, out string cwd, out string sessionDir)
        {
            isSource = false;
            cwd = null;
            sessionDir = null;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            string name = Path.GetFileName(full.TrimEnd('\\', '/'));
            if (LooksLikeSessionDirName(name))
            {
                isSource = true;
                sessionDir = full;
                string got = DirExampleCwd(full);
                if (!string.IsNullOrEmpty(got))
                    cwd = got;
                else
                    cwd = DecodeSessionDirName(name);
                return;
            }

            isSource = false;
            cwd = full;
        }
    }

    // ----------------------------------------------------------- 路徑列 UI
    internal sealed class PathRowPanel : Panel
    {
        private readonly Label _caption;
        private readonly RoundButton _btn;
        private readonly Label _path;
        private Font _uiFont;
        private Font _pathFont;
        private string _pathValue = "";
        private readonly Color _dimColor;
        private readonly Color _textColor;
        private readonly Color _btnBack;

        // 供測試讀取：版面算完後的矩形
        public Rectangle CaptionBounds { get { return _caption.Bounds; } }
        public Rectangle ButtonBounds { get { return _btn.Bounds; } }
        public Rectangle PathBounds { get { return _path.Bounds; } }

        public event Action PickRequested;
        public event Action ClearRequested;

        public PathRowPanel(string caption, Font uiFont, Color textColor, Color dimColor, Color btnBack)
        {
            BackColor = Color.Transparent;
            _dimColor = dimColor;
            _textColor = textColor;
            _btnBack = btnBack;

            _caption = new Label();
            _caption.Text = caption;
            _caption.ForeColor = textColor;
            _caption.AutoSize = true; // 用真實繪製寬度，避免「徑」被裁
            _caption.BackColor = Color.Transparent;
            _caption.TextAlign = ContentAlignment.MiddleLeft;
            _caption.UseCompatibleTextRendering = false;
            _caption.Padding = new Padding(0, 0, 2, 0);

            _btn = new RoundButton();
            _btn.ForeColor = textColor;
            _btn.BackColor = btnBack;
            _btn.FlatStyle = FlatStyle.Flat;
            _btn.FlatAppearance.BorderSize = 0;
            _btn.Cursor = Cursors.Hand;
            _btn.Click += OnBtnClick;
            _btn.Text = "選擇";
            _btn.AutoSize = true;
            _btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btn.UseCompatibleTextRendering = false;
            _btn.Padding = new Padding(10, 4, 10, 4);
            _btn.Margin = new Padding(0);

            _path = new Label();
            _path.AutoSize = false;
            _path.BackColor = Color.Transparent;
            _path.TextAlign = ContentAlignment.MiddleLeft;
            _path.UseCompatibleTextRendering = false;

            Controls.Add(_caption);
            Controls.Add(_btn);
            Controls.Add(_path);

            ApplyFont(uiFont);
        }

        public void ApplyFont(Font uiFont)
        {
            if (uiFont == null) return;
            _uiFont = uiFont;
            if (_pathFont != null)
            {
                try { _pathFont.Dispose(); }
                catch { }
            }
            float pathSize = Math.Max(8f, uiFont.Size - 0.5f);
            try { _pathFont = new Font(uiFont.FontFamily, pathSize, FontStyle.Regular); }
            catch { _pathFont = uiFont; }

            _caption.Font = _uiFont;
            _btn.Font = _uiFont;
            _path.Font = _pathFont;
        }

        public string PathValue
        {
            get { return _pathValue; }
            set
            {
                _pathValue = value == null ? "" : value;
                UpdateButton();
            }
        }

        private void UpdateButton()
        {
            bool has = _pathValue.Length > 0;
            _btn.Text = has ? "清空" : "選擇";
        }

        private void OnBtnClick(object sender, EventArgs e)
        {
            if (_pathValue.Length > 0)
            {
                if (ClearRequested != null) ClearRequested();
            }
            else
            {
                if (PickRequested != null) PickRequested();
            }
        }

        private const int MaxPathLines = 3;

        public void LayoutRow(int contentWidth)
        {
            if (_uiFont == null) return;

            UpdateButton();

            // 先讓 AutoSize 算出真實需要的寬高（含 CJK / DPI）
            _caption.AutoSize = true;
            _btn.AutoSize = true;
            _btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // PreferredSize 在尚未加入可視樹時也可能可用；再加字寬保底
            Size capPref = _caption.PreferredSize;
            Size btnPref = _btn.PreferredSize;

            int gap = Math.Max(10, (int)Math.Ceiling(_uiFont.GetHeight() * 0.45f));

            // 保底：四個漢字寬 + 按鈕兩字寬（避免 Measure/Preferred 仍偏小）
            int em;
            using (Graphics g = CreateGraphics())
            {
                Size emSz = TextRenderer.MeasureText(g, "路徑", _uiFont,
                    Size.Empty, TextFormatFlags.SingleLine);
                em = Math.Max(1, emSz.Width / 2);
            }
            int capW = Math.Max(capPref.Width, em * 4 + 6);
            int btnW = Math.Max(btnPref.Width, em * 2 + 24);
            int rowH = Math.Max(Math.Max(capPref.Height, btnPref.Height) + 8, (int)_uiFont.GetHeight() + 14);

            int pathLeft = capW + gap + btnW + gap;
            int pathW = Math.Max(48, contentWidth - pathLeft);

            string display;
            Color fg;
            if (_pathValue.Length == 0)
            {
                display = "（尚未設定）";
                fg = _dimColor;
            }
            else
            {
                display = DisplayPath(_pathValue);
                fg = _textColor;
            }

            int lines = 1;
            int lineH;
            using (Graphics g = CreateGraphics())
            {
                lineH = Math.Max(
                    TextRenderer.MeasureText(g, "國", _pathFont,
                        Size.Empty, TextFormatFlags.SingleLine).Height,
                    (int)Math.Ceiling(_pathFont.GetHeight()) + 2);

                if (_pathValue.Length > 0 || pathW < 120)
                {
                    List<string> wrapped = WrapPath(display, _pathFont, pathW, g);
                    if (wrapped.Count > MaxPathLines)
                    {
                        // 超過上限：保留前 MaxPathLines 行，最後一行尾端以 ... 取代
                        wrapped.RemoveRange(MaxPathLines, wrapped.Count - MaxPathLines);
                        string last = wrapped[MaxPathLines - 1];
                        while (last.Length > 0 && TextRenderer.MeasureText(g, last + "...", _pathFont,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Width > pathW)
                            last = last.Substring(0, last.Length - 1);
                        wrapped[MaxPathLines - 1] = last + "...";
                    }
                    if (wrapped.Count > 0)
                    {
                        lines = wrapped.Count;
                        display = string.Join("\n", wrapped.ToArray());
                    }
                }
            }

            int pathH = Math.Max(rowH, MaxPathLines * lineH + 4);   // 固定三行高，不隨內容變動
            rowH = Math.Max(rowH, pathH);

            // 固定最終尺寸（不再 AutoSize，避免後續抖動）
            _caption.AutoSize = false;
            _btn.AutoSize = false;

            int capY = Math.Max(0, (rowH - Math.Max(capPref.Height, rowH / 2)) / 2);
            int btnY = Math.Max(0, (rowH - Math.Max(btnPref.Height + 8, 28)) / 2);
            int pathY = Math.Max(0, (rowH - pathH) / 2);
            int capH = rowH;
            int btnH = Math.Max(btnPref.Height + 8, (int)_uiFont.GetHeight() + 12);

            _caption.SetBounds(0, 0, capW, capH);
            _caption.TextAlign = ContentAlignment.MiddleLeft;
            _btn.SetBounds(capW + gap, Math.Max(0, (rowH - btnH) / 2), btnW, btnH);
            _path.SetBounds(pathLeft, pathY, pathW, pathH);
            _path.ForeColor = fg;
            _path.Text = display;
            _path.TextAlign = ContentAlignment.MiddleLeft;
            Height = rowH + 8;
        }

        private static string DisplayPath(string p)
        {
            return p.Replace('\\', '/');
        }

        private static List<string> WrapPath(string text, Font font, int maxWidth, Graphics g)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            int start = 0;
            while (start < text.Length)
            {
                int best = 0;
                for (int len = 1; start + len <= text.Length; len++)
                {
                    string chunk = text.Substring(start, len);
                    Size sz = TextRenderer.MeasureText(g, chunk, font,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.SingleLine);
                    if (sz.Width <= maxWidth) best = len;
                    else break;
                }
                if (best <= 0) best = 1;
                lines.Add(text.Substring(start, best));
                start += best;
            }
            return lines;
        }
    }

    // --------------------------------------------------------------- 主視窗
    internal sealed class MainForm : Form
    {
        private static readonly Color CForm = Color.FromArgb(26, 26, 29);
        private static readonly Color CCard = Color.FromArgb(19, 19, 21);
        private static readonly Color CHint = Color.FromArgb(185, 187, 192);
        private static readonly Color CHintHot = Color.FromArgb(126, 217, 158);
        private static readonly Color CText = Color.FromArgb(222, 222, 222);
        private static readonly Color CDim = Color.FromArgb(140, 142, 148);
        private static readonly Color CAccent = Color.FromArgb(100, 180, 255);
        private static readonly Color CGear = Color.FromArgb(160, 162, 168);
        private static readonly Color CGearHot = Color.FromArgb(230, 230, 230);

        private readonly Config _cfg;
        private readonly DropCard _card;
        private readonly Label _title;
        private readonly PathRowPanel _sourceRow;
        private readonly PathRowPanel _targetRow;
        private readonly Label _hint;
        private readonly GearButton _gear;
        private readonly ToolTip _tip;

        // 執行期狀態（不寫入 config）
        private string _sourceCwd;
        private string _sourceDir;   // 若直接選了 --C--... 資料夾
        private string _targetCwd;

        private bool _working;
        private bool _clickArmed;

        public MainForm(string initialDrop)
        {
            Text = "pi 對話搬移";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 440);
            MinimumSize = new Size(520, 380);
            Padding = new Padding(16);
            BackColor = CForm;
            ForeColor = CText;

            string exeDir;
            try { exeDir = Path.GetDirectoryName(Application.ExecutablePath); }
            catch { exeDir = null; }
            if (string.IsNullOrEmpty(exeDir)) exeDir = Directory.GetCurrentDirectory();
            _cfg = Config.Load(exeDir);

            Font = _cfg.MakeUiFont(1f, FontStyle.Regular);
            try { Icon = AppIcon.CreateWindowIcon(); }
            catch { }
            AllowDrop = true;
            KeyPreview = true;
            DoubleBuffered = true;

            _card = new DropCard();
            _card.Dock = DockStyle.Fill;
            _card.Fill = CCard;
            _card.Radius = 16;
            _card.Padding = new Padding(28, 24, 28, 32);
            _card.AllowDrop = true;
            _card.Cursor = Cursors.Hand;

            float gearSize = Math.Max(18f, _cfg.UiFontSize + 7f);
            _gear = new GearButton();
            int gearBox = Math.Max(34, (int)(gearSize * 1.9f));
            _gear.Size = new Size(gearBox, gearBox);
            _gear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _gear.NormalColor = CGear;
            _gear.HotColor = CGearHot;
            _tip = new ToolTip();
            _tip.SetToolTip(_gear, "開啟設定檔所在位置並選取 move-pi-sessions-config.ini");

            _title = new Label();
            _title.Text = "pi 對話搬移";
            _title.Font = _cfg.MakeUiFont(1.45f, FontStyle.Bold);
            _title.ForeColor = CText;
            _title.AutoSize = true;
            _title.BackColor = Color.Transparent;

            Font rowFont = _cfg.MakeUiFont(1f, FontStyle.Regular);
            _sourceRow = new PathRowPanel("原始路徑", rowFont, CText, CDim, CForm);
            _targetRow = new PathRowPanel("目標路徑", rowFont, CText, CDim, CForm);
            _sourceRow.PickRequested += delegate { PickFolderForSource(true); };
            _sourceRow.ClearRequested += delegate { ClearSource(); };
            _targetRow.PickRequested += delegate { PickFolderForTarget(true); };
            _targetRow.ClearRequested += delegate { ClearTarget(); };

            _hint = new Label();
            _hint.ForeColor = CHint;
            _hint.Font = _cfg.MakeUiFont(0.92f, FontStyle.Regular);
            _hint.AutoSize = false;
            _hint.TextAlign = ContentAlignment.MiddleCenter;
            _hint.BackColor = Color.Transparent;
            _hint.Cursor = Cursors.Hand;

            _card.Controls.Add(_title);
            _card.Controls.Add(_sourceRow);
            _card.Controls.Add(_targetRow);
            _card.Controls.Add(_hint);
            _card.Controls.Add(_gear);
            Controls.Add(_card);

            _card.Resize += delegate { LayoutCard(); };
            LayoutCard();
            SyncPathRows();
            RefreshHint();

            DragEnter += OnDragEnter;
            DragOver += OnDragEnter;
            DragLeave += OnDragLeave;
            DragDrop += OnDragDrop;
            foreach (Control c in new Control[] { _card, _hint, _title, _sourceRow, _targetRow })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnter;
                c.DragOver += OnDragEnter;
                c.DragLeave += OnDragLeave;
                c.DragDrop += OnDragDrop;
            }
            _hint.Click += OnSurfaceClick;
            _card.Click += OnSurfaceClick;
            _title.Click += OnSurfaceClick;

            _gear.Click += OnGearClick;

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };

            if (!string.IsNullOrEmpty(initialDrop))
            {
                Shown += delegate
                {
                    BeginInvoke(new Action(delegate { ApplyDropToNextSlot(initialDrop, true); }));
                };
            }
        }

        private void LayoutCard()
        {
            int w = _card.ClientSize.Width;
            int h = _card.ClientSize.Height;
            int innerW = w - 56;
            _gear.Location = new Point(w - _gear.Width - 26, 22);
            _title.Location = new Point(28, 26);

            // 標題與原始路徑多留空隙
            int y = 28 + Math.Max(40, _title.Height) + 36;
            _sourceRow.SetBounds(28, y, innerW, 40);
            _sourceRow.LayoutRow(innerW);
            y += _sourceRow.Height + 12;
            _targetRow.SetBounds(28, y, innerW, 40);
            _targetRow.LayoutRow(innerW);
            y += _targetRow.Height + 24;
            _hint.SetBounds(28, y, innerW, Math.Max(80, h - y - 36));
        }

        private void SyncPathRows()
        {
            _sourceRow.PathValue = _sourceCwd ?? "";
            _targetRow.PathValue = _targetCwd ?? "";
            LayoutCard();
        }

        private void RefreshHint()
        {
            if (_working) return;
            string what = string.IsNullOrEmpty(_sourceCwd) ? "來源路徑" : "目標路徑";
            _hint.Text = "拖入資料夾，或點擊此區選擇" + what;
            _hint.ForeColor = CHint;
            _card.Hot = false;
        }

        private void ClearSource()
        {
            _sourceCwd = null;
            _sourceDir = null;
            SyncPathRows();
            RefreshHint();
        }

        private void ClearTarget()
        {
            _targetCwd = null;
            SyncPathRows();
            RefreshHint();
        }

        private void AssignSourceFromPath(string path, bool tryRun, string reason)
        {
            bool isSession;
            string cwd, sessionDir;
            PiPaths.ClassifyDrop(path, _cfg.ResolveSessionsRoot(),
                out isSession, out cwd, out sessionDir);

            if (isSession)
            {
                if (string.IsNullOrEmpty(cwd))
                {
                    MessageBox.Show(this, "無法從這個 session 資料夾解析來源 cwd。",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _sourceCwd = cwd;
                _sourceDir = sessionDir;
            }
            else
            {
                try { _sourceCwd = PiPaths.NormalizeCwd(path); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "路徑無效：\r\n" + ex.Message,
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _sourceDir = null;
            }

            SyncPathRows();
            RefreshHint();
            if (tryRun && !string.IsNullOrEmpty(_sourceCwd) && !string.IsNullOrEmpty(_targetCwd))
                TryConvert(reason);
        }

        private void AssignTargetFromPath(string path, bool tryRun, string reason)
        {
            try { _targetCwd = PiPaths.NormalizeCwd(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "路徑無效：\r\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SyncPathRows();
            RefreshHint();
            if (tryRun && !string.IsNullOrEmpty(_sourceCwd) && !string.IsNullOrEmpty(_targetCwd))
                TryConvert(reason);
        }

        private void ApplyDropToNextSlot(string path, bool tryRun)
        {
            if (string.IsNullOrEmpty(_sourceCwd))
                AssignSourceFromPath(path, tryRun, "已設定來源");
            else if (string.IsNullOrEmpty(_targetCwd))
                AssignTargetFromPath(path, tryRun, "已設定目標");
            else
                AssignTargetFromPath(path, tryRun, "已更新目標");
        }

        private void PickFolderForSource(bool tryRun)
        {
            string picked = BrowseFolder(
                "選擇原始路徑（搬移前的專案資料夾）",
                InitialBrowsePath(true));
            if (picked == null) return;
            AssignSourceFromPath(picked, tryRun, "已設定來源");
        }

        private void PickFolderForTarget(bool tryRun)
        {
            string picked = BrowseFolder(
                "選擇目標路徑（搬移後的專案資料夾）",
                InitialBrowsePath(false));
            if (picked == null) return;
            AssignTargetFromPath(picked, tryRun, "已設定目標");
        }

        private string InitialBrowsePath(bool forSource)
        {
            if (forSource)
            {
                if (!string.IsNullOrEmpty(_sourceCwd) && Directory.Exists(_sourceCwd))
                    return _sourceCwd;
                string root = _cfg.ResolveSessionsRoot();
                if (Directory.Exists(root)) return root;
            }
            else
            {
                if (!string.IsNullOrEmpty(_targetCwd) && Directory.Exists(_targetCwd))
                    return _targetCwd;
                if (!string.IsNullOrEmpty(_sourceCwd) && Directory.Exists(_sourceCwd))
                    return _sourceCwd;
            }
            return null;
        }

        private string BrowseFolder(string description, string initial)
        {
            try
            {
                return ExplorerFolderPicker.Pick(this, description, initial);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "無法開啟資料夾選擇對話框：\r\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.Apply(Handle);
        }

        // ------------------------------------------------------------ 拖曳
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                _card.Hot = true;
                _hint.ForeColor = CHintHot;
                _hint.Text = "放開以套用路徑";
            }
            else e.Effect = DragDropEffects.None;
        }

        private void OnDragLeave(object sender, EventArgs e)
        {
            RefreshHint();
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            _card.Hot = false;
            RefreshHint();
            string[] files = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string dir = null;
            foreach (string f in files)
            {
                if (Directory.Exists(f)) { dir = Path.GetFullPath(f); break; }
            }
            if (dir == null) return;
            ApplyDropToNextSlot(dir, true);
        }

        // ------------------------------------------------------------ 齒輪 = move-pi-sessions-config.ini
        private void OnGearClick(object sender, EventArgs e)
        {
            _clickArmed = true;
            try
            {
                string fp = _cfg.FilePath;
                if (File.Exists(fp))
                    ShellReveal.SelectPath(fp);
                else
                {
                    string dir = Path.GetDirectoryName(fp);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        ShellReveal.SelectPath(dir);
                }
            }
            catch { }
            BeginInvoke(new Action(delegate { _clickArmed = false; }));
        }

        // ------------------------------------------------------------ 點擊空白 = 依序選路徑
        private void OnSurfaceClick(object sender, EventArgs e)
        {
            if (_clickArmed || _working) return;
            if (string.IsNullOrEmpty(_sourceCwd))
                PickFolderForSource(true);
            else if (string.IsNullOrEmpty(_targetCwd))
                PickFolderForTarget(true);
            else
                PickFolderForTarget(true);
        }

        // ------------------------------------------------------------ 轉換
        private void TryConvert(string reason)
        {
            if (_working) return;
            if (string.IsNullOrEmpty(_sourceCwd) || string.IsNullOrEmpty(_targetCwd))
                return;

            if (PiPaths.PathKey(_sourceCwd) == PiPaths.PathKey(_targetCwd))
            {
                MessageBox.Show(this, "來源與目標是同一個路徑，不需要搬移。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string sessionsRoot = _cfg.ResolveSessionsRoot();
            if (!Directory.Exists(sessionsRoot))
            {
                MessageBox.Show(this,
                    "找不到 sessions 目錄：\r\n" + sessionsRoot,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string srcDir = null;
            if (!string.IsNullOrEmpty(_sourceDir) && Directory.Exists(_sourceDir))
            {
                srcDir = _sourceDir;
            }
            else
            {
                List<string> candidates = PiPaths.FindSessionDirs(sessionsRoot, _sourceCwd);
                if (candidates.Count == 0)
                {
                    MessageBox.Show(this,
                        "在 sessions 底下找不到來源對應的對話目錄。\r\n\r\n"
                        + "來源 cwd：\r\n" + _sourceCwd + "\r\n\r\n"
                        + "預期目錄名：\r\n" + PiPaths.EncodeSessionDirName(_sourceCwd),
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (candidates.Count == 1)
                    srcDir = candidates[0];
                else
                {
                    srcDir = PickCandidate(candidates);
                    if (srcDir == null) return;
                }
            }

            string tgtDir = PiPaths.FindTargetDir(sessionsRoot, _targetCwd);
            string newCwd = _targetCwd;
            if (Directory.Exists(tgtDir))
            {
                string borrowed = PiPaths.DirExampleCwd(tgtDir);
                if (!string.IsNullOrEmpty(borrowed)
                    && PiPaths.PathKey(borrowed) == PiPaths.PathKey(_targetCwd))
                    newCwd = borrowed;
            }

            if (PiPaths.PathKey(srcDir) == PiPaths.PathKey(tgtDir))
            {
                MessageBox.Show(this, "來源與目標是同一個 session 目錄，不需要搬移。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<string> files = PiPaths.CollectFiles(srcDir);
            if (files.Count == 0)
            {
                MessageBox.Show(this, "來源目錄裡沒有對話檔。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> conflicts = new List<string>();
            foreach (string f in files)
            {
                string dest = Path.Combine(tgtDir, Path.GetFileName(f));
                if (File.Exists(dest)) conflicts.Add(Path.GetFileName(f));
            }
            if (conflicts.Count > 0)
            {
                MessageBox.Show(this,
                    "目標目錄已有同名檔案，為避免蓋掉資料，請先自行處理：\r\n\r\n"
                    + string.Join("\r\n", conflicts.ToArray()),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string msg =
                (string.IsNullOrEmpty(reason) ? "" : reason + "\r\n\r\n")
                + "來源 cwd：\r\n  " + _sourceCwd + "\r\n\r\n"
                + "目標 cwd：\r\n  " + newCwd + "\r\n\r\n"
                + "來源目錄：\r\n  " + Path.GetFileName(srcDir) + "\r\n\r\n"
                + "待搬對話：" + files.Count + " 個\r\n\r\n"
                + "確定要搬移？";

            DialogResult ans = MessageBox.Show(this, msg, "確認搬移",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (ans != DialogResult.Yes) return;

            RunMove(srcDir, tgtDir, newCwd, files);
        }

        private string PickCandidate(List<string> candidates)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "選擇來源目錄";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(520, 280);
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.ShowInTaskbar = false;
                dlg.Font = Font;

                Label tip = new Label();
                tip.Text = "找到多個可能的來源目錄，請挑一個：";
                tip.SetBounds(16, 12, 480, 24);

                ListBox lb = new ListBox();
                lb.SetBounds(16, 40, 488, 170);
                foreach (string d in candidates)
                {
                    int n = 0;
                    try { n = Directory.GetFiles(d, "*.jsonl").Length; }
                    catch { }
                    lb.Items.Add(Path.GetFileName(d) + "  （" + n + " 個對話）");
                }
                if (lb.Items.Count > 0) lb.SelectedIndex = 0;

                Button ok = new Button();
                ok.Text = "確定";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(320, 230, 88, 30);

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(416, 230, 88, 30);

                dlg.Controls.Add(tip);
                dlg.Controls.Add(lb);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                if (lb.SelectedIndex < 0 || lb.SelectedIndex >= candidates.Count) return null;
                return candidates[lb.SelectedIndex];
            }
        }

        private void RunMove(string srcDir, string tgtDir, string newCwd, List<string> files)
        {
            _working = true;
            _hint.Text = "搬移中…";
            _hint.ForeColor = CAccent;
            Application.DoEvents();

            bool createdTgt = !Directory.Exists(tgtDir);
            try { Directory.CreateDirectory(tgtDir); }
            catch (Exception ex)
            {
                _working = false;
                SyncPathRows();
                RefreshHint();
                MessageBox.Show(this, "無法建立目標目錄：\r\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int ok = 0;
            List<string> failed = new List<string>();
            foreach (string f in files)
            {
                string dest = Path.Combine(tgtDir, Path.GetFileName(f));
                string err = PiPaths.MoveOne(f, dest, newCwd);
                if (err == null) ok++;
                else failed.Add(Path.GetFileName(f) + "：" + err);
            }

            bool removedSrc = false;
            try
            {
                if (ok > 0 && Directory.Exists(srcDir)
                    && Directory.GetFileSystemEntries(srcDir).Length == 0)
                {
                    Directory.Delete(srcDir);
                    removedSrc = true;
                    if (PiPaths.PathKey(srcDir) == PiPaths.PathKey(_sourceDir))
                        _sourceDir = null;
                }
            }
            catch { }

            if (createdTgt && ok == 0)
            {
                try
                {
                    if (Directory.Exists(tgtDir)
                        && Directory.GetFileSystemEntries(tgtDir).Length == 0)
                        Directory.Delete(tgtDir);
                }
                catch { }
            }

            _working = false;
            SyncPathRows();
            RefreshHint();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("完成：成功 " + ok + " 個，失敗 " + failed.Count + " 個");
            sb.AppendLine();
            sb.AppendLine("新位置：");
            sb.AppendLine(tgtDir);
            if (removedSrc)
            {
                sb.AppendLine();
                sb.AppendLine("來源目錄已清空並移除。");
            }
            if (failed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("失敗清單：");
                foreach (string line in failed) sb.AppendLine("  " + line);
            }
            sb.AppendLine();
            sb.Append("在目標路徑底下執行 pi 即可看到這些對話。");

            MessageBox.Show(this, sb.ToString(), Text,
                MessageBoxButtons.OK,
                failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    // ----------------------------------------------------------- 拖曳卡片（虛線 + 熱態）
    internal sealed class DropCard : Panel
    {
        public Color Fill = Color.FromArgb(19, 19, 21);
        public int Radius = 16;
        private static readonly Color BorderIdle = Color.FromArgb(90, 160, 162, 168);
        private static readonly Color BorderHot = Color.FromArgb(180, 126, 217, 158);
        private static readonly Color TintHot = Color.FromArgb(28, 126, 217, 158);

        private bool _hot;

        public bool Hot
        {
            get { return _hot; }
            set
            {
                if (_hot == value) return;
                _hot = value;
                Invalidate();
            }
        }

        public DropCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Width -= 1; r.Height -= 1;
            using (GraphicsPath path = RoundRect(r, Radius))
            {
                Color fill = Fill;
                if (_hot)
                {
                    // 熱態：底色加一層淡綠
                    using (SolidBrush br = new SolidBrush(Fill))
                        e.Graphics.FillPath(br, path);
                    using (SolidBrush tint = new SolidBrush(TintHot))
                        e.Graphics.FillPath(tint, path);
                }
                else
                {
                    using (SolidBrush br = new SolidBrush(fill))
                        e.Graphics.FillPath(br, path);
                }

                using (Pen pen = new Pen(_hot ? BorderHot : BorderIdle, _hot ? 2.4f : 1.8f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    pen.DashPattern = new float[] { 5f, 4f };
                    e.Graphics.DrawPath(pen, path);
                }
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ----------------------------------------------------------- 圓角按鈕（抗鋸齒自繪）
    internal sealed class RoundButton : Button
    {
        private bool _over;
        private bool _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _over = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 父層多為 Transparent，往上找到真正有底色的容器（DropCard.Fill / 一般 BackColor）
            Color parentBack = SystemColors.Control;
            for (Control c = Parent; c != null; c = c.Parent)
            {
                DropCard card = c as DropCard;
                if (card != null) { parentBack = card.Fill; break; }
                if (c.BackColor.A == 255) { parentBack = c.BackColor; break; }
            }
            g.Clear(parentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill = BackColor;
            if (_down) fill = ControlPaint.Dark(fill, 0.05f);
            else if (_over) fill = ControlPaint.Light(fill, 0.15f);

            int radius = Math.Max(6, Height / 3);
            float d = radius * 2f;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = new GraphicsPath())
            using (SolidBrush br = new SolidBrush(fill))
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(br, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // ----------------------------------------------------------- 齒輪按鈕（六齒盾形、實心、中間圓孔鏤空）
    internal sealed class GearButton : Control
    {
        public Color NormalColor = Color.FromArgb(160, 162, 168);
        public Color HotColor = Color.FromArgb(230, 230, 230);
        private bool _over;

        public GearButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(36, 36);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _over = true; Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _over = false; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Color c = _over ? HotColor : NormalColor;
            float scale = Math.Min(Width, Height);
            GearShape.Fill(g, c, Width / 2f, Height / 2f, scale * 0.86f);
        }
    }

    // 六齒「盾形」齒輪：齒根寬、齒尖窄的梯形齒，實心本體 + 中間圓孔鏤空
    internal static class GearShape
    {
        public static void Fill(Graphics g, Color c, float cx, float cy, float size)
        {
            float tipR = size * 0.50f;
            float rootR = size * 0.36f;
            float holeR = size * 0.17f;
            float round = Math.Max(1f, size * 0.07f);   // 圓角：用同色圓角描邊
            tipR -= round / 2f;

            using (GraphicsPath outer = BuildOuter(cx, cy, tipR, rootR, 6))
            using (GraphicsPath body = (GraphicsPath)outer.Clone())
            using (SolidBrush br = new SolidBrush(c))
            using (Pen pen = new Pen(c, round))
            {
                pen.LineJoin = LineJoin.Round;
                body.AddEllipse(cx - holeR, cy - holeR, holeR * 2f, holeR * 2f);
                body.FillMode = FillMode.Alternate;   // 外形 + 內圓 → 圓孔鏤空
                g.FillPath(br, body);
                g.DrawPath(pen, outer);               // 只描外緣，讓齒角變圓
            }
        }

        private static GraphicsPath BuildOuter(float cx, float cy, float tipR, float rootR, int teeth)
        {
            double period = 2.0 * Math.PI / teeth;
            double halfRoot = period * 0.30;   // 齒根半寬（寬）
            double halfTip = period * 0.16;    // 齒尖半寬（窄）
            List<PointF> pts = new List<PointF>();
            for (int k = 0; k < teeth; k++)
            {
                double a = k * period - Math.PI / 2.0;
                pts.Add(Pt(cx, cy, rootR, a - halfRoot));
                pts.Add(Pt(cx, cy, tipR, a - halfTip));
                pts.Add(Pt(cx, cy, tipR, a + halfTip));
                pts.Add(Pt(cx, cy, rootR, a + halfRoot));
                // 齒間谷底沿齒根圓取樣
                double from = a + halfRoot, to = a + period - halfRoot;
                for (int i = 1; i < 4; i++)
                    pts.Add(Pt(cx, cy, rootR, from + (to - from) * i / 4.0));
            }
            GraphicsPath path = new GraphicsPath();
            path.AddPolygon(pts.ToArray());
            return path;
        }

        private static PointF Pt(float cx, float cy, float r, double ang)
        {
            return new PointF(cx + (float)(Math.Cos(ang) * r), cy + (float)(Math.Sin(ang) * r));
        }
    }

    // ----------------------------------------------------- 應用程式圖示（pi 搬移資料夾）
    // 全程式碼繪製：視窗圖示直接用；exe 圖示由 `--make-ico 路徑` 輸出，csc-build.cmd 編譯時自動呼叫。
    internal static class AppIcon
    {
        private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

        public static Bitmap Render(int S)
        {
            Bitmap bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color front = Color.FromArgb(0xFF, 0xC8, 0x4A);
                Color ink = Color.FromArgb(0x6B, 0x3F, 0x00);

                // 單層資料夾（左上頁籤 + 矩形本體）
                FillRounded(g, S, front, new PointF[] {
                    P(S, 0.05f, 0.18f), P(S, 0.38f, 0.18f), P(S, 0.46f, 0.29f),
                    P(S, 0.95f, 0.29f), P(S, 0.95f, 0.84f), P(S, 0.05f, 0.84f) });

                // π（畫在本體中央）
                using (Pen pp = new Pen(ink, Math.Max(1.5f, S * 0.08f)))
                {
                    pp.StartCap = LineCap.Round; pp.EndCap = LineCap.Round; pp.LineJoin = LineJoin.Round;
                    g.DrawLine(pp, S * 0.31f, S * 0.45f, S * 0.69f, S * 0.45f);
                    g.DrawLine(pp, S * 0.42f, S * 0.45f, S * 0.38f, S * 0.71f);
                    g.DrawLine(pp, S * 0.58f, S * 0.45f, S * 0.62f, S * 0.71f);
                }
            }
            return bmp;
        }

        private static PointF P(int S, float x, float y) { return new PointF(S * x, S * y); }

        // 多邊形 + 同色圓角描邊 = 圓角多邊形
        private static void FillRounded(Graphics g, int S, Color c, PointF[] pts)
        {
            float w = S * 0.06f;
            // 內縮半個描邊寬度，避免圓角描邊讓外形變大
            float cx = 0, cy = 0;
            foreach (PointF q in pts) { cx += q.X; cy += q.Y; }
            cx /= pts.Length; cy /= pts.Length;
            PointF[] ins = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                float dx = pts[i].X - cx, dy = pts[i].Y - cy;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                float k = len > 0 ? (len - w / 2f) / len : 1f;
                ins[i] = new PointF(cx + dx * k, cy + dy * k);
            }
            using (SolidBrush br = new SolidBrush(c))
            using (Pen pen = new Pen(c, w))
            {
                pen.LineJoin = LineJoin.Round;
                g.FillPolygon(br, ins);
                g.DrawPolygon(pen, ins);
            }
        }

        public static Icon CreateWindowIcon()
        {
            using (Bitmap bmp = Render(64))
            {
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        public static void WriteIco(string file)
        {
            List<byte[]> pngs = new List<byte[]>();
            foreach (int s in Sizes)
            {
                using (Bitmap b = Render(s))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            using (FileStream fs = File.Create(file))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    byte dim = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
                    bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32);
                    bw.Write((uint)pngs[i].Length); bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) bw.Write(p);
            }
        }
    }

    // ------------------------------------------------------------- 深色標題列
    internal static class Dwm
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(IntPtr hwnd)
        {
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
            }
            catch { }
        }
    }
}
