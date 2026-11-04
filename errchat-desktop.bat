@echo off
rem errchat for desktop apps: type a message, press Ctrl+Alt+G for a better version.
title errchat
cd /d "%~dp0server"
.venv\Scripts\python.exe -m errchat_server.desktop
pause
