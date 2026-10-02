using System; using System.IO; using System.Diagnostics; using System.Linq; using System.Text.RegularExpressions;
class FfmpegShim {
    static string GetLatestDir(string appName){
        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop\\apps\\" + appName);
        // 路徑不存在就自動建立並回傳 null，避免 GetDirectories 噴例外
        if(!System.IO.Directory.Exists(appRoot)){
            try { System.IO.Directory.CreateDirectory(appRoot); } catch {}
            return null;
        }
        try {
            var dirs = System.IO.Directory.GetDirectories(appRoot).Where(d => System.IO.Path.GetFileName(d) != "current");
            if(!dirs.Any()) return null;
            return dirs.OrderByDescending(d => {
                var fileName = System.IO.Path.GetFileName(d);
                var nums = System.Text.RegularExpressions.Regex.Matches(fileName, "\\d+").Cast<System.Text.RegularExpressions.Match>().Select(m => int.Parse(m.Value).ToString("D8"));
                return nums.Any() ? string.Join(".", nums) : fileName;
            }).FirstOrDefault();
        } catch {
            return null;
        }
    }

    static int Main(string[] args){
        var latest = GetLatestDir("kdenlive");
        if(latest == null || !Directory.Exists(latest)) return 1;
        var exe = Path.Combine(latest, "bin", "ffmpeg.exe");
        if(!File.Exists(exe)) return 1;
        var psi = new ProcessStartInfo(exe, String.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a))){
            UseShellExecute=false,
            CreateNoWindow=false,
            RedirectStandardOutput=false,
            RedirectStandardError=false,
            RedirectStandardInput=false
        };
        var p = Process.Start(psi);
        p.WaitForExit();
        return p.ExitCode;
    }
}
