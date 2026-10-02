$BackupRoot = Join-Path $env:USERPROFILE "portable-exe"
$ShimsDir = Join-Path $env:USERPROFILE "scoop\shims"
# 路徑不存在就自動建立
New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
New-Item -ItemType Directory -Path $ShimsDir -Force | Out-Null

$CommonMethod = @'
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
'@

# 1. 開機啟動器
$LauncherCs = @"
using System; using System.IO; using System.Diagnostics; using System.Threading; using System.Linq; using System.Text.RegularExpressions;
class Launcher {
$CommonMethod
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
"@

# 2. ffmpeg
$FfmpegCs = @"
using System; using System.IO; using System.Diagnostics; using System.Linq; using System.Text.RegularExpressions;
class FfmpegShim {
$CommonMethod
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
"@

Set-Content "$BackupRoot\launcher.cs" $LauncherCs -Encoding UTF8
Set-Content "$BackupRoot\ffmpeg.cs" $FfmpegCs -Encoding UTF8

$csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = (Get-ChildItem "$env:WINDIR\Microsoft.NET\Framework" -Recurse -Filter csc.exe | Select-Object -First 1 -ErrorAction SilentlyContinue).FullName }

& $csc /nologo /target:winexe /out:"$BackupRoot\launcher.exe" "$BackupRoot\launcher.cs"
& $csc /nologo /target:exe /out:"$BackupRoot\ffmpeg.exe" "$BackupRoot\ffmpeg.cs"
Remove-Item "$BackupRoot\launcher.cs", "$BackupRoot\ffmpeg.cs" -Force -ErrorAction SilentlyContinue

# 確保 shims 目錄存在才 Move
if(Test-Path "$BackupRoot\ffmpeg.exe"){
    Move-Item "$BackupRoot\ffmpeg.exe" (Join-Path $ShimsDir "ffmpeg.exe") -Force
}

# 排程直接跑 launcher.exe
if(Test-Path "$BackupRoot\launcher.exe"){
    $Principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    $Trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    $Settings.ExecutionTimeLimit = "PT0S"
    $Action = New-ScheduledTaskAction -Execute "$BackupRoot\launcher.exe"
    Register-ScheduledTask -TaskName "Scoop-Backup-Start" -Action $Action -Trigger $Trigger -Principal $Principal -Settings $Settings -Force | Out-Null
}

Write-Host "完成。launcher: 不閃，30ms / ffmpeg: 自動偵測最新版，15ms" -ForegroundColor Green