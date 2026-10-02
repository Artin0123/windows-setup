using System; using System.IO; using System.Diagnostics; using System.Threading; using System.Linq; using System.Text.RegularExpressions;
// 需以 winexe 編譯（setup.ps1 已指定），否則開機會閃出主控台視窗
class Launcher {
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

    static void StartHidden(string exe, string args, string workDir){
        if(!File.Exists(exe)) return;
        Process.Start(new ProcessStartInfo(exe, args){ WorkingDirectory=workDir, CreateNoWindow=true, UseShellExecute=false, WindowStyle=ProcessWindowStyle.Hidden });
    }
    static void Main(){
        var sDir = GetLatestDir("syncthing");
        if(sDir != null){
            StartHidden(Path.Combine(sDir, "syncthing.exe"), "--no-console --no-browser", sDir);
        }
        var rDir = GetLatestDir("rclone");
        if(rDir != null){
            StartHidden(Path.Combine(rDir, "rclone.exe"), "mount InfiniCLOUD: D: --vfs-disk-space-total-size 40G --vfs-cache-mode writes --dir-cache-time 1m --poll-interval 0 --vfs-cache-max-size 1G --network-mode --no-console --rc --rc-no-auth", rDir);
        }
        for(int i=0; i<30; i++){ if(Directory.Exists("D:\\")) break; Thread.Sleep(1000); }
    }
}
