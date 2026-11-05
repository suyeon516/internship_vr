@echo off
rem Space collection local web server: http://localhost:8000
cd /d "%~dp0"
start "" http://localhost:8000
py -m http.server 8000
