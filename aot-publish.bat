@echo off
rem Temporary helper: NativeAOT publish with a hand-built MSVC environment.
rem VS 2026's VC install here has cl/link but no vcvarsall.bat and is invisible
rem to ILC's vswhere discovery, so PATH/LIB are set manually.
set "VCDIR=C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Tools\MSVC\14.51.36231"
set "SDKLIB=C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0"
set "PATH=%VCDIR%\bin\Hostx64\x64;%PATH%"
rem Only the onecore flavor of the VC libs is installed; fine for Win10/11 desktop.
set "LIB=%VCDIR%\lib\onecore\x64;%SDKLIB%\ucrt\x64;%SDKLIB%\um\x64"
cd /d D:\REPO\Github\BluSee
dotnet publish src\BluSee -c Release -p:PublishProfile=aot-win-x64 -p:IlcUseEnvironmentalTools=true
