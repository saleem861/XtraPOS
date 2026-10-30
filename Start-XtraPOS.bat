@echo off
start "XtraPOS API" cmd /k "cd /d D:\Google\XtraPOS\src\XtraPOS.Api && dotnet run --launch-profile http"
start "XtraPOS Web" cmd /k "cd /d D:\Google\XtraPOS\src\XtraPOS.Web && npm run dev -- --host 127.0.0.1 --port 5173"
timeout /t 12 /nobreak >nul
start "" "http://127.0.0.1:5173/"
