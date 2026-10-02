using System; using System.IO; using System.Diagnostics; using System.Linq; using System.Text.RegularExpressions;
class FfmpegShim {
    static string GetLatestDir(string appName){
        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop\\apps\\" + appName);
        try {
            if(!Directory.Exists(appRoot)) return null;
            var dirs = Directory.GetDirectories(appRoot).Where(d => Path.GetFileName(d) != "current");
            // 版本號每段數字補零到 20 位再比較，避免 int 溢位（例如時間戳版本）
            return dirs.OrderByDescending(d => {
                var name = Path.GetFileName(d);
                var nums = Regex.Matches(name, "\\d+").Cast<Match>().Select(m => m.Value.PadLeft(20, '0')).ToArray();
                return nums.Length > 0 ? string.Join(".", nums) : name;
            }, StringComparer.Ordinal).FirstOrDefault();
        } catch {
            return null;
        }
    }

    // 取出原始命令列中「執行檔之後」的部分，原樣轉交，保留所有引號與空字串引數
    static string RawArgs(){
        var cl = Environment.CommandLine;
        int i = 0;
        if(cl.Length > 0 && cl[0] == '"'){
            i = cl.IndexOf('"', 1);
            i = i < 0 ? cl.Length : i + 1;
        } else {
            while(i < cl.Length && !char.IsWhiteSpace(cl[i])) i++;
        }
        return cl.Substring(i).TrimStart();
    }

    static int Main(string[] args){
        var latest = GetLatestDir("kdenlive");
        if(latest == null) return 1;
        var exe = Path.Combine(latest, "bin", "ffmpeg.exe");
        if(!File.Exists(exe)) return 1;
        var psi = new ProcessStartInfo(exe, RawArgs()){
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
