@echo off
REM ═══════════════════════════════════════════════════════════════════════════════════════════
REM  Phat hanh ban Windows qua Velopack + GitHub Releases (repo PUBLIC -> client khong can token).
REM  Chay o MAY DEV. Thay cho publish-suite.cmd khi muon RA BAN MOI cho fleet tu cap nhat.
REM  Bump phien ban: sua version.txt (vd 1.0.0 -> 1.0.1) roi chay script nay.
REM ═══════════════════════════════════════════════════════════════════════════════════════════
setlocal
cd /d "%~dp0"

REM ── KIEM LOI: dung "|| goto :fail", TUYET DOI KHONG dung "if errorlevel 1" ────────────────────
REM   "if errorlevel 1" nghia la "errorlevel >= 1" nen MA LOI AM lot luoi sach: vpk tra ve -1 khi
REM   that bai (vd "There is a release ... equal or greater to the current version" / "already a
REM   remote asset named releases.win.json"), -1 < 1 nen script chay tiep va in "DA PHAT HANH" du
REM   KHONG co gi len GitHub (da dinh that 13/08). "||" bat MOI ma khac 0, va chay dung ca trong
REM   khoi ngoac - khac %ERRORLEVEL% (bi no ngay luc doc khoi, khong phai luc chay).
REM   Moi buoc dat %STAGE% truoc khi chay de :fail noi DUNG buoc nao hong (dung do tai buoc khac).

set STAGE=doc version.txt
if not exist version.txt goto :fail
set VER=
set /p VER=<version.txt
REM  set /p GIU NGUYEN bien cu khi file rong/thieu -> khong xoa VER truoc thi co the phat hanh bang
REM  version RAC thua ke tu moi truong cua shell goi script.
if not defined VER goto :fail

set REPO=https://github.com/muinx2022/shopee-suite
set OUT=Releases
set PUB=publish\ShopeeSuite
set NODELTA=

echo === Phat hanh ShopeeSuite v%VER% (win-x64) ===

REM 0a) CHAN BAY TAG DONG NHAM COMMIT: "vpk upload --tag" gan tag vao HEAD cua nhanh mac dinh TREN
REM     GITHUB tai thoi diem upload, KHONG phai commit local. Chua push la tag tro vao commit CU
REM     -^> mat dau vet ban phat hanh gan voi ma nguon. Da hong that 3 ban: v1.8.9, v1.9.0, v1.9.1.
REM     Kiem SOM (truoc build) de khoi ton ~3 phut publish+pack roi moi bao "chua push".
if not defined GITHUB_TOKEN goto :bo_qua_kiem_push
set STAGE=kiem da push chua (chan tag dong nham commit)
git rev-parse --verify HEAD >nul 2>&1 || goto :khonggit
git fetch -q origin || goto :fail
for /f %%i in ('git rev-parse HEAD') do set LOCALSHA=%%i
for /f %%i in ('git rev-parse origin/main') do set REMOTESHA=%%i
if not "%LOCALSHA%"=="%REMOTESHA%" goto :chuapush
goto :bo_qua_kiem_push
:khonggit
echo [canh bao] Khong kiem duoc git ^(khong phai repo?^) -^> bo qua buoc chan tag-dong-nham-commit.
:bo_qua_kiem_push

REM 0) Ban copy shared\ cua 3 extension phai con khop extensions\shared\ (nguon chuan). Lech -> DUNG,
REM    vi extension nap thang tu thu muc cua no nen ban dong goi se chay code cu.
set STAGE=kiem ban copy shared\ cua 3 extension
call extensions\sync-shared.cmd --check || goto :fail

REM 1) Keo ban cu ve de tao DELTA (repo public -> khong can token). Lan dau chua co -> di tiep,
REM    NHUNG ghi co NODELTA de nhac lai o CUOI (canh bao in o day se troi mat sau vai phut log build).
set STAGE=keo ban cu ve de tao delta
vpk download github --repoUrl %REPO% --channel win --outputDir %OUT% || set NODELTA=1

REM 2) Publish self-contained (R2R) — giu nguyen nhu publish-suite.cmd.
REM    rmdir phai KIEM lai: "dotnet publish -o" KHONG don thu muc dich, nen rmdir hong mot phan
REM    (file dang bi khoa) la file DOI CU lot vao goi phat hanh — dung thu ma buoc 0 sinh ra de chan.
set STAGE=don thu muc publish
if exist "%PUB%" rmdir /s /q "%PUB%"
if exist "%PUB%" goto :fail
set STAGE=publish (dotnet publish)
dotnet publish suite\Shopee.Suite\Shopee.Suite.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "%PUB%" || goto :fail

REM 3) Dong goi Velopack: Setup.exe + goi full + goi delta.
REM    KY SO co dieu kien: co signing\trusted-signing.json -> ky qua Azure Trusted Signing (can `az login` truoc).
REM    Khong co -> pack CHUA KY (may client bat Smart App Control se chan; xem signing\README.md).
set SIGN=
if exist "signing\trusted-signing.json" (
  set SIGN=--azureTrustedSignFile signing\trusted-signing.json
  echo [ky so] Dung Azure Trusted Signing ^(signing\trusted-signing.json^). Nho da chay `az login`.
) else (
  echo [canh bao] CHUA cau hinh ky so ^(signing\trusted-signing.json^) -^> ban CHUA KY. Xem signing\README.md.
)
set STAGE=dong goi Velopack (vpk pack)
vpk pack --packId ShopeeSuite --packTitle "Shopee Suite" --packAuthors "Shopee Suite" --packVersion %VER% --packDir "%PUB%" --mainExe ShopeeSuite.exe --icon assets\app-icon.ico --channel win --outputDir %OUT% %SIGN% || goto :fail

REM 4) Day len GitHub Releases neu co GITHUB_TOKEN (quyen ghi repo). Khong co token -> chi dong goi cuc bo.
if not defined GITHUB_TOKEN goto :khongday
set STAGE=day len GitHub (vpk upload)
echo === Dang day len GitHub Releases ... ===
vpk upload github --repoUrl %REPO% --channel win --outputDir %OUT% --publish true --merge true --releaseName "Shopee Suite v%VER%" --tag v%VER% --token %GITHUB_TOKEN% || goto :fail
echo === DA PHAT HANH v%VER% ^(win^) len GitHub ===
if defined NODELTA echo [canh bao] Ban nay KHONG co goi delta ^(buoc 1 khong keo duoc ban cu^) -^> moi may trong fleet phai tai FULL ~123 MB.
goto :eof

:khongday
echo.
echo Da dong goi xong vao %OUT%\ nhung CHUA day len GitHub ^(thieu bien moi truong GITHUB_TOKEN^).
echo   set GITHUB_TOKEN=^<token co quyen ghi repo^>  roi chay lai script,
echo   hoac chay tay: vpk upload github --repoUrl %REPO% --channel win --outputDir %OUT% --publish true --merge true --releaseName "Shopee Suite v%VER%" --tag v%VER% --token ^<TOKEN^>
if defined NODELTA echo [canh bao] Goi vua dong KHONG co delta ^(buoc 1 khong keo duoc ban cu^).
goto :eof

:chuapush
echo.
echo *** DUNG: commit local CHUA PUSH len origin/main ***
echo     local  HEAD        = %LOCALSHA%
echo     origin/main        = %REMOTESHA%
echo     "vpk upload --tag" gan tag vao HEAD TREN GITHUB, nen day luc nay se dong tag v%VER% vao
echo     commit CU. Chay "git push origin main" roi chay lai script nay.
exit /b 1

:fail
echo.
echo *** THAT BAI o buoc: %STAGE% ***
echo     Xem dong [FTL] / loi gan nhat ben tren.
if "%STAGE%"=="dong goi Velopack (vpk pack)" (
  echo     Hay gap nhat: "There is a release ... equal or greater to the current version %VER%"
  echo     = ban %VER% DA phat hanh roi. Velopack khong cho de len ban da publish -^> bump version.txt
  echo     len so moi roi chay lai ^(muon thay ban cu that su thi phai xoa release + tag tren GitHub^).
)
if "%STAGE%"=="doc version.txt" (
  echo     version.txt thieu hoac rong -^> KHONG doan version de tranh phat hanh nham so.
)
if "%STAGE%"=="don thu muc publish" (
  echo     Xoa "%PUB%" khong sach ^(file dang bi khoa?^). "dotnet publish -o" KHONG don thu muc dich,
  echo     nen di tiep la file DOI CU lot vao goi phat hanh. Dong app/Explorer dang giu file roi chay lai.
)
if "%STAGE%"=="day len GitHub (vpk upload)" (
  echo     LUU Y: upload day TUNG asset mot, nen no CO THE da tao release/tag va up mot phan roi moi
  echo     chet. DUNG cho rang "chua co gi len GitHub" — kiem bang:  gh release view v%VER%
  echo     Neu thay release nua voi thi xoa han release + tag roi lam lai.
) else (
  echo     Buoc nay chay o may local, chua co gi len GitHub.
)
exit /b 1
