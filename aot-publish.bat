@echo off
rem Temporary helper: NativeAOT publish with a hand-built MSVC environment.
rem VS 2026's VC install here has cl/link but no vcvarsall.bat and is invisible
rem to ILC's vswhere discovery, so PATH/LIB are set manually.
set "VCDIR=C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Tools\MSVC\14.51.36231"
set "SDKLIB=C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0"
set "PATH=%VCDIR%\bin\Hostx64\x64;%PATH%"
rem Only the onecore flavor of the VC libs is installed; fine for Win10/11 desktop.
set "LIB=%VCDIR%\lib\onecore\x64;%SDKLIB%\ucrt\x64;%SDKLIB%\um\x64"
rem Run from the repo root wherever it is checked out; output goes to publish\ (git-ignored).
cd /d "%~dp0"
dotnet publish src\BluSee -c Release -r win-x64 -p:PublishAot=true -p:IlcUseEnvironmentalTools=true -o publish
