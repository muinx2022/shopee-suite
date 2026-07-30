@echo off
REM ═══════════════════════════════════════════════════════════════════════════════════════════
REM  Chep extensions\shared\*.js sang extensions\shopee-*\shared\ - ban copy checked-in cua tung ext.
REM  Vi sao phai copy: extension duoc nap THANG tu thu muc cua no bang --load-extension, khong the
REM  import nguoc ra ngoai thu muc extension, nen moi extension phai giu mot ban copy trong repo.
REM
REM    extensions\sync-shared.cmd           : CHEP - chay sau moi lan sua extensions\shared\
REM    extensions\sync-shared.cmd --check   : chi KIEM TRA lech, exit 1 neu co - release-suite.cmd goi
REM ═══════════════════════════════════════════════════════════════════════════════════════════
setlocal
cd /d "%~dp0"

set MODE=copy
if /i "%~1"=="--check" set MODE=check

set DRIFT=0

REM  Bang phan phoi: extension nao dung module nao - them module moi thi them dong o day.
call :one shopee-search util.js
call :one shopee-search ws-bridge.js
call :one shopee-search tab-wait.js
call :one shopee-search net-detect.js
call :one shopee-scrape util.js
call :one shopee-scrape tab-wait.js
call :one shopee-scrape net-detect.js
call :one shopee-orders util.js
call :one shopee-orders ws-bridge.js
call :one shopee-orders tab-wait.js
call :one shopee-orders dbg-input.js

if "%MODE%"=="check" goto :verdict
echo [sync-shared] Da chep xong shared\ cho 3 extension.
goto :eof

:verdict
if "%DRIFT%"=="0" goto :clean
echo *** LECH ban shared: %DRIFT% file. Chay extensions\sync-shared.cmd roi commit lai. ***
REM  endlocal TRUOC exit /b: endlocal ngam luc thoat script se nuot ma loi cua exit /b.
endlocal
exit /b 1

:clean
echo [sync-shared] OK - cac ban copy khop extensions\shared\.
goto :eof

:one
set EXT=%~1
set FILE=%~2
set SRC=shared\%FILE%
set DST=%EXT%\shared\%FILE%
if "%MODE%"=="check" (
  if not exist "%DST%" (
    echo   [thieu] %DST%
    set /a DRIFT+=1
    goto :eof
  )
  fc /b "%SRC%" "%DST%" >nul 2>&1
  if errorlevel 1 (
    echo   [lech]  %DST%
    set /a DRIFT+=1
  )
) else (
  if not exist "%EXT%\shared" mkdir "%EXT%\shared"
  copy /y "%SRC%" "%DST%" >nul
)
goto :eof
