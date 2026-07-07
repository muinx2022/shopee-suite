@echo off
REM ═══════════════════════════════════════════════════════════════════════════════════════════
REM  Phat hanh ban Windows qua Velopack + GitHub Releases (repo PUBLIC -> client khong can token).
REM  Chay o MAY DEV. Thay cho publish-suite.cmd khi muon RA BAN MOI cho fleet tu cap nhat.
REM  Bump phien ban: sua version.txt (vd 1.0.0 -> 1.0.1) roi chay script nay.
REM ═══════════════════════════════════════════════════════════════════════════════════════════
setlocal
cd /d "%~dp0"

set /p VER=<version.txt
set REPO=https://github.com/muinx2022/shopee-suite
set OUT=Releases
set PUB=publish\ShopeeSuite

echo === Phat hanh ShopeeSuite v%VER% (win-x64) ===

REM 1) Keo ban cu ve de tao DELTA (repo public -> khong can token). Lan dau chua co -> bo qua loi.
vpk download github --repoUrl %REPO% --channel win --outputDir %OUT%

REM 2) Publish self-contained (R2R) — giu nguyen nhu publish-suite.cmd.
if exist "%PUB%" rmdir /s /q "%PUB%"
dotnet publish suite\Shopee.Suite\Shopee.Suite.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "%PUB%"
if errorlevel 1 goto :fail

REM 3) Dong goi Velopack: Setup.exe + goi full + goi delta.
vpk pack --packId ShopeeSuite --packTitle "Shopee Suite" --packAuthors "Shopee Suite" --packVersion %VER% --packDir "%PUB%" --mainExe ShopeeSuite.exe --icon assets\app-icon.ico --channel win --outputDir %OUT%
if errorlevel 1 goto :fail

REM 4) Day len GitHub Releases neu co GITHUB_TOKEN (quyen ghi repo). Khong co token -> chi dong goi cuc bo.
if defined GITHUB_TOKEN (
  echo === Dang day len GitHub Releases ... ===
  vpk upload github --repoUrl %REPO% --channel win --outputDir %OUT% --publish true --merge true --releaseName "Shopee Suite v%VER%" --tag v%VER% --token %GITHUB_TOKEN%
  if errorlevel 1 goto :fail
  echo === DA PHAT HANH v%VER% (win) len GitHub ===
) else (
  echo.
  echo Da dong goi xong vao %OUT%\ nhung CHUA day len GitHub ^(thieu bien moi truong GITHUB_TOKEN^).
  echo   set GITHUB_TOKEN=^<token co quyen ghi repo^>  roi chay lai script,
  echo   hoac chay tay: vpk upload github --repoUrl %REPO% --channel win --outputDir %OUT% --publish true --merge true --releaseName "Shopee Suite v%VER%" --tag v%VER% --token ^<TOKEN^>
)
goto :eof

:fail
echo *** THAT BAI — xem log ben tren ***
exit /b 1
