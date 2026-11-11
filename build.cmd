@echo off
rem Builds Radiocity against .NET Framework 4.0 using the in-box C# compiler.
rem
rem No NuGet, no SDK, no project file: this is the same toolchain a Windows XP SP3
rem machine has after installing .NET 4.0. csc.exe from the runtime directory does
rem not need the "reference assemblies" targeting pack, which is why this works on
rem machines (and CI images) that never had Visual Studio installed.
rem
rem Usage:  build.cmd [Release|Debug]      default is Release

setlocal

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

set OUTDIR=%~dp0bin
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found. Install .NET Framework 4.0.
    exit /b 1
)

set ICON=%~dp0assets\radiocity.ico
if not exist "%ICON%" (
    echo Icon missing. Run: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
    exit /b 1
)

set FLAGS=/nologo /target:exe /platform:anycpu /unsafe+ /langversion:4 /warn:4
if /i "%CONFIG%"=="Debug" (
    set FLAGS=%FLAGS% /debug+ /optimize-
) else (
    set FLAGS=%FLAGS% /optimize+
)

rem The web UI is embedded so Radiocity.exe stays a single file you can copy to
rem another machine. The resource name is what Page.cs looks up.
"%CSC%" %FLAGS% ^
    /out:"%OUTDIR%\Radiocity.exe" ^
    /win32icon:"%ICON%" ^
    /resource:"%~dp0web\index.html",Radiocity.web.index.html ^
    "%~dp0src\Dsp\RingBuffer.cs" ^
    "%~dp0src\Dsp\Nco.cs" ^
    "%~dp0src\Dsp\Fir.cs" ^
    "%~dp0src\Dsp\Fft.cs" ^
    "%~dp0src\Dsp\Demod.cs" ^
    "%~dp0src\Dsp\FmDemod.cs" ^
    "%~dp0src\Dsp\FmStereo.cs" ^
    "%~dp0src\Dsp\SsbDemod.cs" ^
    "%~dp0src\Dsp\ClockCalib.cs" ^
    "%~dp0src\Dsp\DecimationChain.cs" ^
    "%~dp0src\Dsp\Squelch.cs" ^
    "%~dp0src\Dsp\NoiseBlanker.cs" ^
    "%~dp0src\Dsp\IqImbalance.cs" ^
    "%~dp0src\Dsp\IqFile.cs" ^
    "%~dp0src\Dsp\Dcf77.cs" ^
    "%~dp0src\Dsp\Receiver.cs" ^
    "%~dp0src\Audio\WinMM.cs" ^
    "%~dp0src\Web\HttpServer.cs" ^
    "%~dp0src\Web\SampleSource.cs" ^
    "%~dp0src\Web\Page.cs" ^
    "%~dp0src\Web\CaptureHub.cs" ^
    "%~dp0src\Web\RadioSession.cs" ^
    "%~dp0src\Web\QuadratureSource.cs" ^
    "%~dp0src\Web\WebRadio.cs" ^
    "%~dp0src\App\WavFile.cs" ^
    "%~dp0src\App\Program.cs" ^
    "%~dp0src\App\AssemblyInfo.cs"

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo built %OUTDIR%\Radiocity.exe (%CONFIG%)
endlocal
