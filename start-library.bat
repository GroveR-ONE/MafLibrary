@echo off
setlocal
cd /d "%~dp0"
if "%MAF_ADMIN_PIN%"=="" set MAF_ADMIN_PIN=admin
set "MAF_DATA=%~dp0data"
echo.
echo  MAF Product Library
echo  Server: http://localhost:5080
echo  LAN:    http://IP-ADDRESS:5080   (printed below after the server starts)
echo  PIN:    %MAF_ADMIN_PIN%
echo  Data:   %MAF_DATA%
echo.
echo  Keep this window open while using the library.
echo.
dotnet run --no-launch-profile
pause
