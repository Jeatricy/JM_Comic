@echo off
chcp 65001 >nul 2>&1
echo ==========================================
echo    Build Single-EXE JMComic Client
echo ==========================================
echo.

echo [1/5] Installing PyInstaller...
pip install pyinstaller --break-system-packages -q
if errorlevel 1 (
    echo [ERROR] PyInstaller installation failed
    pause
    exit /b 1
)

echo.
echo [2/5] Building Python backend as exe...
cd /d "%~dp0backend"
pyinstaller --onefile --name JMComicBackend --add-data "requirements.txt;." server.py 2>&1
if errorlevel 1 (
    echo [ERROR] PyInstaller failed
    pause
    exit /b 1
)
echo [OK] Backend exe built

echo.
echo [3/5] Copying backend exe to frontend resources...
if not exist "%~dp0backend\dist\JMComicBackend.exe" (
    echo [ERROR] Backend executable was not produced
    pause
    exit /b 1
)
mkdir "%~dp0frontend\Resources" 2>nul
copy /y "%~dp0backend\dist\JMComicBackend.exe" "%~dp0frontend\Resources\" >nul
if errorlevel 1 (
    echo [ERROR] Could not copy backend executable into frontend resources
    pause
    exit /b 1
)
echo [OK] Copied

echo.
echo [4/5] Building single-file WPF exe...
cd /d "%~dp0frontend"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0publish" 2>&1
if errorlevel 1 (
    echo [ERROR] .NET build failed
    pause
    exit /b 1
)
echo [OK] Frontend built

echo.
echo [5/5] Cleaning up...
rd /s /q "%~dp0frontend\Resources" 2>nul
rd /s /q "%~dp0backend\dist" 2>nul
rd /s /q "%~dp0backend\build" 2>nul
del /q "%~dp0backend\JMComicBackend.spec" 2>nul

echo.
echo ==========================================
echo  Done! Output: publish\JMComicClient.exe
echo ==========================================
echo.
pause
