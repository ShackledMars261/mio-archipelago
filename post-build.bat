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

rem Files that must not ship, for two reasons.
rem
rem PolyHook: mio-mod-loader has its own copy and loads it before we initialize,
rem so a second one here is at best ignored and at worst a managed binding that
rem doesn't match the native it binds to.
rem
rem cimgui: left over from when the GUI went through ImGui.NET. We render into
rem the game's own ImGui now and no package pulls it in, so a clean build won't
rem produce it - this line only matters until bin\ has been cleared out.
rem
rem Needed at all because the glob above is indiscriminate and TARGET_DIR keeps
rem files from earlier builds that MSBuild doesn't track and so never removes.
for %%F in (
	asmjit.dll
	asmtk.dll
	cimgui.dll
	cpolyhook2.dll
	PolyHook_2.dll
	PolyHook2.NET.dll
	Zydis.dll
) do (
	if exist "%STAGE_DIR%\%%F" (
		del /Q "%STAGE_DIR%\%%F" || exit /b 1
		echo Skipped %%F
	)
)

endlocal