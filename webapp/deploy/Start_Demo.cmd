@echo off
rem Runs the workload portal on this PC with the fictional demonstration data (SQLite, pick-a-user sign-in).
rem Needs the ASP.NET Core 8 Runtime: https://dotnet.microsoft.com/download/dotnet/8.0
rem Put this file in the folder extracted from workload-portal.zip, then double-click it.
cd /d "%~dp0"
set ASPNETCORE_ENVIRONMENT=Development
start "" http://localhost:5080
dotnet Enppi.Workload.Api.dll --urls http://localhost:5080
pause
