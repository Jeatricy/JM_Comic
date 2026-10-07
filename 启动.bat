@echo off
chcp 65001 >nul 2>&1
cd /d "%~dp0"
echo ==========================================
echo          JMComic - Dev Mode
echo ==========================================
echo.

echo Current dir: %cd%
echo.

python --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python not found
    pause
    exit /b 1
)

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found
    pause
    exit /b 1
)

echo [1/4] Installing Python dependencies...
cd /d "%~dp0backend"
pip install -r requirements.txt --break-system-packages -q
echo [OK]

echo.
echo [2/4] Starting Python backend (port 5000)...
start "JMComic Backend" python server.py
echo [OK]

echo.
echo [3/4] Waiting for backend...
:wait_loop
timeout /t 1 /nobreak >nul
curl -s http://127.0.0.1:5000/api/health >nul 2>&1
if errorlevel 1 goto wait_loop
echo [OK] Backend ready

echo.
echo [4/4] Starting WPF frontend...
cd /d "%~dp0frontend"
dotnet run --configuration Release

echo.
echo Frontend closed, stopping backend...
taskkill /FI "WindowTitle eq JMComic Backend*" /T /F >nul 2>&1
echo Done
pause
