@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

rem =============================================================================
rem  csc-build.cmd - drop a .cs onto this file to build an .exe next to it
rem
rem  Usage:
rem    1) Drag one or more .cs files onto this file
rem    2) Or on the command line: csc-build.cmd path\to\App.cs [more.cs...]
rem    2b) In a terminal add /nopause first so it never waits for a key:
rem        csc-build.cmd /nopause move_pi_sessions.cs   (or set CSC_NOPAUSE=1)
rem    3) Optional first argument is the target type: exe | winexe | library
rem       e.g. csc-build.cmd winexe App.cs Helper.cs
rem
rem  Defaults:
rem    - Output goes next to the first .cs, named after it
rem    - winexe is chosen automatically when WinForms/WPF is detected, else exe
rem    - Common .NET FX 4.x assemblies are referenced; add your own via CSC_EXTRA
rem      set CSC_EXTRA=/r:C:\libs\Foo.dll
rem    - Define symbols: set CSC_DEFINE=MODE_TRANSFER
rem =============================================================================

rem Terminal / CI: pass /nopause as the first argument (or set CSC_NOPAUSE=1) to skip all pauses.
set "NOPAUSE=%CSC_NOPAUSE%"
if /i "%~1"=="/nopause" set "NOPAUSE=1" & shift /1

if "%~1"=="" (
  echo.
  echo 把 .cs 拖到「%~nx0」上，或：
  echo   %~nx0 [exe^|winexe^|library] file.cs [file2.cs ...]
  echo.
  if not defined NOPAUSE pause
  exit /b 1
)

rem --- Find csc.exe: Framework64 4.0, then Framework, then VS Roslyn ---
set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not defined CSC (
  for %%R in (
    "%ProgramFiles%\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe"
    "%ProgramFiles%\Microsoft Visual Studio\2019\*\MSBuild\Current\Bin\Roslyn\csc.exe"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\*\MSBuild\Current\Bin\Roslyn\csc.exe"
    "%ProgramFiles(x86)%\MSBuild\*\Bin\Roslyn\csc.exe"
  ) do if not defined CSC if exist %%~R set "CSC=%%~R"
)

if not defined CSC (
  echo [錯誤] 找不到 csc.exe。請確認已安裝 .NET Framework 4.x。
  if not defined NOPAUSE pause
  exit /b 1
)

rem --- Optional target override ---
set "TARGET="
if /i "%~1"=="exe"     set "TARGET=exe"     & shift /1
if /i "%~1"=="winexe"  set "TARGET=winexe"  & shift /1
if /i "%~1"=="library" set "TARGET=library" & shift /1
if /i "%~1"=="dll"     set "TARGET=library" & shift /1

if "%~1"=="" (
  echo [錯誤] 沒有指定 .cs 檔。
  if not defined NOPAUSE pause
  exit /b 1
)

set "FIRST=%~1"
if not exist "%FIRST%" (
  echo [錯誤] 找不到：%FIRST%
  if not defined NOPAUSE pause
  exit /b 1
)

set "OUTDIR=%~dp1"
set "OUTBASE=%~n1"
if /i "%TARGET%"=="library" (
  set "OUTFILE=%OUTDIR%%OUTBASE%.dll"
) else (
  set "OUTFILE=%OUTDIR%%OUTBASE%.exe"
)

rem --- Collect all .cs; if no target given, guess winexe from content ---
set "SOURCES="
set "GUESS_WIN=0"
:collect
if "%~1"=="" goto collected
if /i not "%~x1"==".cs" (
  echo [略過] 不是 .cs：%~1
  shift /1
  goto collect
)
if not exist "%~1" (
  echo [錯誤] 找不到：%~1
  if not defined NOPAUSE pause
  exit /b 1
)
set "SOURCES=!SOURCES! "%~1""
if not defined TARGET (
  findstr /i /m /c:"System.Windows.Forms" /c:"System.Windows.Application" /c:"[STAThread]" "%~1" >nul 2>&1 && set "GUESS_WIN=1"
)
shift /1
goto collect

:collected
if not defined SOURCES (
  echo [錯誤] 沒有有效的 .cs 檔。
  if not defined NOPAUSE pause
  exit /b 1
)

if not defined TARGET (
  if "!GUESS_WIN!"=="1" (set "TARGET=winexe") else (set "TARGET=exe")
)

set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Xml.dll /r:System.Data.dll"
set "DEFINES="
if defined CSC_DEFINE set "DEFINES=/define:%CSC_DEFINE%"

rem Variants: source lines like  "// csc-variant: NAME DEFINE"  build NAME.exe with /define:DEFINE
rem (one .cs -> several exes). Without such lines, a single exe is built as before.
rem NOTE: no labels / call :sub here - UTF-8 + chcp 65001 makes cmd mis-seek labels.
set "NVAR=0"
for /f "tokens=3,4" %%a in ('findstr /c:"// csc-variant:" "%FIRST%"') do (
  set /a NVAR+=1
  set "VO!NVAR!=%OUTDIR%%%a.exe"
  set "VD!NVAR!=/define:%%b"
)
if "!NVAR!"=="0" (
  set "NVAR=1"
  set "VO1=%OUTFILE%"
  set "VD1=%DEFINES%"
)

for /l %%i in (1,1,!NVAR!) do (
  rem Icon per output: a same-named .ico next to the output wins; otherwise, if the source
  rem contains "--make-ico", build a temp exe with the same defines, let it draw the .ico, embed it.
  set "ICONOPT="
  set "TMPICO="
  set "TMPEXE="
  for %%p in ("!VO%%i!") do set "VBASE=%%~dpnp"
  if exist "!VBASE!.ico" (
    set ICONOPT="/win32icon:!VBASE!.ico"
  ) else if /i not "%TARGET%"=="library" (
    findstr /m /c:"--make-ico" "%FIRST%" >nul 2>&1 && (
      set "TMPEXE=%TEMP%\csc_icogen_!RANDOM!.exe"
      set "TMPICO=%TEMP%\csc_icogen_!RANDOM!.ico"
      "%CSC%" /nologo /utf8output /codepage:65001 /target:winexe /out:"!TMPEXE!" %REFS% !VD%%i! %CSC_EXTRA% %SOURCES% >nul 2>&1
      if exist "!TMPEXE!" (
        start "" /wait "!TMPEXE!" --make-ico "!TMPICO!"
        del /q "!TMPEXE!" >nul 2>&1
      )
      if exist "!TMPICO!" set ICONOPT="/win32icon:!TMPICO!"
    )
  )

  echo.
  echo === build ===
  echo   csc    : %CSC%
  echo   target : %TARGET%
  echo   out    : !VO%%i!
  if defined ICONOPT echo   icon   : !ICONOPT!
  if defined VD%%i echo   define : !VD%%i!
  if defined CSC_EXTRA echo   extra  : %CSC_EXTRA%
  echo.
  "%CSC%" /nologo /utf8output /codepage:65001 /optimize+ /target:%TARGET% /out:"!VO%%i!" !ICONOPT! %REFS% !VD%%i! %CSC_EXTRA% %SOURCES%
  set "ERR=!ERRORLEVEL!"
  if defined TMPICO del /q "!TMPICO!" >nul 2>&1
  if not "!ERR!"=="0" (
    echo [FAILED]
    if not defined NOPAUSE pause
    exit /b 1
  )
  echo [OK] !VO%%i!
)

echo.
if not defined NOPAUSE pause
exit /b 0
