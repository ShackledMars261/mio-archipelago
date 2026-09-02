@echo off
setlocal

set TARGET_FILE=%~1
set PROJECT_NAME=%~2
set TARGET_DIR=%~3
set STAGE_DIR=%cd%\bin\%PROJECT_NAME%

if exist "%STAGE_DIR%" rd /S /Q "%STAGE_DIR%"
if exist "%STAGE_DIR%" (
	echo ERROR: could not clear "%STAGE_DIR%" - is the game running or a file locked?
	exit /b 1
)
md "%STAGE_DIR%" || exit /b 1

xcopy "%cd%\Mod" "%STAGE_DIR%" /E /I /H /Y || exit /b 1
xcopy "%TARGET_DIR%*.dll" "%STAGE_DIR%" /Y || exit /b 1

endlocal