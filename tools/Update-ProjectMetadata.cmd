@echo off
rem SimFlow 项目档案批量修改（双击运行；所有交互在 PowerShell 里，避免 cmd 解析中文出错）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-ProjectMetadata.ps1" -Menu
if errorlevel 1 pause
