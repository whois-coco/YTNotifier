@echo off
echo === YTNotifier 発行スクリプト ===
echo.
echo [1/3] クリーン中...
rd /s /q bin 2>nul
rd /s /q obj 2>nul
echo クリーン完了
echo.
echo [2/3] ビルド確認...
dotnet build -c Release
if %errorlevel% neq 0 (
    echo ビルドエラーが発生しました
    pause
    exit /b 1
)
echo.
echo [3/3] 発行中...
dotnet publish -c Release -r win-x64 --self-contained -o ./publish
if %errorlevel% neq 0 (
    echo 発行エラーが発生しました
    pause
    exit /b 1
)
echo.
echo === 発行完了: ./publish フォルダ ===
pause
