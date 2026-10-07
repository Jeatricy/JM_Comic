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
if errorlevel 1 (
    echo [ERROR] Python dependency installation failed
    pause
    exit /b 1
)
echo [OK]

echo.
echo [2/4] Starting Python backend (port 5000)...
for /f "delims=" %%T in ('python -c "import secrets; print(secrets.token_urlsafe(32))"') do set "JMCOMIC_API_TOKEN=%%T"
if not defined JMCOMIC_API_TOKEN (
    echo [ERROR] Could not create backend API token
    pause
    exit /b 1
)
start "JMComic Backend" python server.py
if errorlevel 1 (
    echo [ERROR] Could not start Python backend
    pause
    exit /b 1
)
echo [OK]

echo.
echo [3/4] Waiting for backend...
set /a wait_count=0
:wait_loop
timeout /t 1 /nobreak >nul
curl -s -H "X-JMComic-Token: %JMCOMIC_API_TOKEN%" http://127.0.0.1:5000/api/health >nul 2>&1
if not errorlevel 1 goto backend_ready
set /a wait_count+=1
if %wait_count% GEQ 30 goto backend_failed
goto wait_loop

:backend_failed
echo [ERROR] Backend did not become ready within 30 seconds
taskkill /FI "WindowTitle eq JMComic Backend*" /T /F >nul 2>&1
pause
exit /b 1

:backend_ready
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
