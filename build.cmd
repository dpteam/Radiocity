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

set FLAGS=/nologo /target:exe /platform:anycpu /unsafe+ /langversion:4 /warn:4
if /i "%CONFIG%"=="Debug" (
    set FLAGS=%FLAGS% /debug+ /optimize-
) else (
    set FLAGS=%FLAGS% /optimize+
)

"%CSC%" %FLAGS% ^
    /out:"%OUTDIR%\Radiocity.exe" ^
    /win32icon:"%ICON%" ^
    "%~dp0src\Dsp\RingBuffer.cs" ^
    "%~dp0src\Dsp\Nco.cs" ^
    "%~dp0src\Dsp\Fir.cs" ^
    "%~dp0src\Dsp\Fft.cs" ^
    "%~dp0src\Dsp\Demod.cs" ^
    "%~dp0src\Dsp\FmDemod.cs" ^
    "%~dp0src\Dsp\SsbDemod.cs" ^
    "%~dp0src\Dsp\ClockCalib.cs" ^
    "%~dp0src\Dsp\Dcf77.cs" ^
    "%~dp0src\Dsp\Receiver.cs" ^
    "%~dp0src\Audio\WinMM.cs" ^
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
