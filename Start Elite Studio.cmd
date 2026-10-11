@echo off
rem Starts Elite Studio from the dist folder (the ready-to-run copy).
if not exist "%~dp0dist\EliteStudio.exe" (
  echo Elite Studio has not been built yet: the dist folder is missing.
  echo Download the ready-to-run zip from https://github.com/rmatlock0520-cell/Elite-Studio/releases
  pause
  exit /b 1
)
start "" "%~dp0dist\EliteStudio.exe"
