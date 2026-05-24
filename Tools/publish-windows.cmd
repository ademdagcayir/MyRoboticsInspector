@echo off
REM ================================================================
REM  MyRoboticsInspector — Windows Self-Contained Publish + Velopack
REM ================================================================
REM  Kullanim: publish-windows.cmd <version>
REM     ornek:  publish-windows.cmd 1.0.1
REM
REM  Cikti:
REM    publish\win-x64\        — self-contained binary klasoru (~150 MB)
REM    Releases\Setup.exe       — kullanicilara dagitilan ilk-kurulum installer'i
REM    Releases\*-full.nupkg    — sunucuya yuklenmek uzere full paket
REM    Releases\*-delta.nupkg   — sunucuya yuklenmek uzere delta paket (kucuk)
REM    Releases\RELEASES        — manifest dosyasi (sunucuya yuklemek SART)
REM
REM  Sunucu: Releases\ icindekileri olduğu gibi
REM    https://myrobotics.com.tr/updates/inspector/
REM    altina yukle (web server'in directory listing'i aktif olmasin gerekir).
REM ================================================================
setlocal EnableDelayedExpansion

set "APPID=MyRoboticsInspector"
set "TITLE=My Robotics Inspector"
set "AUTHORS=My Robotics"
set "VERSION=%~1"

if "%VERSION%"=="" (
    echo HATA: surum numarasi gerekli.
    echo.
    echo Kullanim:  publish-windows.cmd ^<version^>
    echo    ornek:  publish-windows.cmd 1.0.1
    echo.
    exit /b 1
)

REM vpk (Velopack CLI) yuklu mu?
where vpk >nul 2>&1
if errorlevel 1 (
    echo === vpk Velopack CLI yuklenmesi gerekiyor ===
    dotnet tool install -g vpk
    if errorlevel 1 (
        echo HATA: vpk yuklenemedi.
        exit /b 1
    )
)

set "ROOT=%~dp0.."
set "PUBLISH=%ROOT%\publish\win-x64"
set "RELEASES=%ROOT%\Releases"
set "CSPROJ=%ROOT%\MyRoboticsInspector.csproj"

echo.
echo === [1/3] Eski publish klasoru temizleniyor ===
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"

echo.
echo === [2/3] dotnet publish (self-contained, win-x64, %VERSION%) ===
dotnet publish "%CSPROJ%" ^
    -f net9.0-windows10.0.19041.0 ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -o "%PUBLISH%" ^
    -p:Version=%VERSION% ^
    -p:ApplicationDisplayVersion=%VERSION% ^
    -p:WindowsPackageType=None
if errorlevel 1 (
    echo HATA: dotnet publish basarisiz.
    exit /b 1
)

echo.
echo === [3/3] vpk pack (Velopack delta/installer uretimi) ===
vpk pack ^
    -u "%APPID%" ^
    -v %VERSION% ^
    -p "%PUBLISH%" ^
    -e MyRoboticsInspector.exe ^
    --packTitle "%TITLE%" ^
    --packAuthors "%AUTHORS%" ^
    -o "%RELEASES%"
if errorlevel 1 (
    echo HATA: vpk pack basarisiz.
    exit /b 1
)

echo.
echo ================================================================
echo  BASARILI — Surum %VERSION% paketlendi.
echo ================================================================
echo.
echo  Dagitim adimlari:
echo    1. Releases\ icindekilerin TAMAMINI sunucuya yukle:
echo       %RELEASES%\* ^=^>  https://myrobotics.com.tr/updates/inspector/
echo    2. Ilk-kurulum kullanicilarina Setup.exe'yi gonder
echo    3. Mevcut kurulumlu kullanicilar uygulamanin Ayarlar'inda
echo       "Simdi Kontrol Et" buton ile guncellemeyi gorur, indir+yeniden baslat
echo.
echo  Test:
echo    %RELEASES%\Setup.exe   ^<-- bu kuruluma tikla ^& dene
echo.
endlocal
