@echo off
rem Pull collection.json + photos from the headset (USB, developer mode) into SpaceDex\data
rem If you change Package Name in Player Settings, change PKG too
set PKG=com.UnityTechnologies.com.unity.template.urpblank
set ADB=adb
where adb >nul 2>nul || set ADB="C:\Program Files\Unity\Hub\Editor\6000.0.83f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
cd /d "%~dp0"
if not exist data mkdir data
%ADB% pull /sdcard/Android/data/%PKG%/files/SpaceDex/. data
pause
