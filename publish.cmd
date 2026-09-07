@echo off
setlocal
rem Builds a release of GMExplorer as one self-contained executable in .\publish.
rem The .NET runtime, Avalonia's native libraries and gmnative.dll all end up inside it.

rem The native analyser has to exist before the publish, so it can be embedded.
set "MSBUILD="
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do set "MSBUILD=%%i"

if defined MSBUILD (
    echo Building gmnative...
    "%MSBUILD%" native\gmnative.vcxproj -p:Configuration=Release -p:Platform=x64 -v:m -nologo || goto :fail
) else (
    echo No Visual Studio C++ toolchain found - publishing without the YYC analyser.
)

echo Publishing...
dotnet publish GMExplorer\GMExplorer.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -p:SatelliteResourceLanguages=en ^
    -o publish || goto :fail

echo.
dir /b publish
goto :eof

:fail
echo.
echo Build failed.
exit /b 1
