@echo off
cd /d "%~dp0"
REM Publish hong ma van in "Xong" la dev chay lai EXE cua lan publish TRUOC -> test nham code cu.
REM Dung "|| goto :fail", KHONG dung "if errorlevel 1" (xem giai thich o release-suite.cmd).
dotnet publish suite\Shopee.Suite\Shopee.Suite.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "publish\ShopeeSuite" || goto :fail
echo.
echo === Xong: publish\ShopeeSuite\ShopeeSuite.exe ===
pause
goto :eof

:fail
echo.
echo *** PUBLISH HONG - KHONG co ban moi. EXE trong publish\ShopeeSuite ^(neu co^) la cua lan truoc. ***
pause
exit /b 1
