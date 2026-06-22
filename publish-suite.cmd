@echo off
cd /d "%~dp0"
dotnet publish suite\Shopee.Suite\Shopee.Suite.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "publish\ShopeeSuite"
echo.
echo === Xong: publish\ShopeeSuite\ShopeeSuite.exe ===
pause
