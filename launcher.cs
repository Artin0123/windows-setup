using System; using System.IO; using System.Diagnostics; using System.Threading; using System.Linq; using System.Text.RegularExpressions;
class Launcher {
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

    static void StartHidden(string exe, string args, string workDir){
        if(!File.Exists(exe)) return;
        if(!Directory.Exists(workDir)){
            try { Directory.CreateDirectory(workDir); } catch {}
        }
        Process.Start(new ProcessStartInfo(exe, args){ WorkingDirectory=workDir, CreateNoWindow=true, UseShellExecute=false, WindowStyle=ProcessWindowStyle.Hidden });
    }
    static void Main(){
        var sDir = GetLatestDir("syncthing");
        if(sDir != null && Directory.Exists(sDir)){
            var sExe = Path.Combine(sDir, "syncthing.exe");
            if(File.Exists(sExe)) StartHidden(sExe, "--no-console --no-browser", sDir);
        }
        var rDir = GetLatestDir("rclone");
        if(rDir != null && Directory.Exists(rDir)){
            var rExe = Path.Combine(rDir, "rclone.exe");
            if(File.Exists(rExe)) StartHidden(rExe, "mount InfiniCLOUD: D: --vfs-disk-space-total-size 40G --vfs-cache-mode writes --dir-cache-time 1m --poll-interval 0 --vfs-cache-max-size 1G --network-mode --no-console --rc --rc-no-auth", rDir);
        }
        for(int i=0; i<30; i++){ if(Directory.Exists("D:\\")) break; Thread.Sleep(1000); }
    }
}
