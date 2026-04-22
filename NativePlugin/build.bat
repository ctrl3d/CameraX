@echo off
setlocal enableextensions enabledelayedexpansion

rem ===================================================================
rem  oes_renderer .so build script (Windows)
rem
rem  Requirements:
rem    * Android NDK r23+  (ANDROID_NDK_HOME or ANDROID_NDK_ROOT)
rem    * Unity Editor      (UNITY_EDITOR_ROOT env var, default below)
rem
rem  Output: Assets\Plugins\Android\libs\<abi>\liboes_renderer.so
rem ===================================================================

rem Accept either ANDROID_NDK_HOME or ANDROID_NDK_ROOT.
if "%ANDROID_NDK_HOME%"=="" if not "%ANDROID_NDK_ROOT%"=="" set "ANDROID_NDK_HOME=%ANDROID_NDK_ROOT%"

if "%ANDROID_NDK_HOME%"=="" (
    echo [build.bat] ANDROID_NDK_HOME is not set.
    exit /b 1
)

if not exist "%ANDROID_NDK_HOME%\build\cmake\android.toolchain.cmake" (
    echo [build.bat] ERROR: android.toolchain.cmake not found under
    echo                    %ANDROID_NDK_HOME%\build\cmake\
    echo             ANDROID_NDK_HOME seems wrong. Expected NDK root, e.g.
    echo             C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\NDK
    exit /b 1
)

if "%UNITY_EDITOR_ROOT%"=="" set "UNITY_EDITOR_ROOT=C:\Program Files\Unity\Hub\Editor\6000.3.13f1"

rem --- Use Unity-bundled cmake + ninja so users do not need their own ---
set "UNITY_CMAKE_BIN=%UNITY_EDITOR_ROOT%\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\cmake\3.22.1\bin"
if exist "%UNITY_CMAKE_BIN%\cmake.exe" (
    set "PATH=%UNITY_CMAKE_BIN%;%PATH%"
    echo [build.bat] Using Unity-bundled cmake at %UNITY_CMAKE_BIN%
) else (
    echo [build.bat] WARNING: Unity cmake not found at %UNITY_CMAKE_BIN%
    echo             Falling back to whatever 'cmake' is on PATH.
)

where cmake >nul 2>&1 || (echo [build.bat] ERROR: cmake not found on PATH. & exit /b 1)
where ninja >nul 2>&1 || (echo [build.bat] ERROR: ninja not found on PATH. & exit /b 1)

rem %~dp0 has a trailing backslash. Appending a "." sidesteps the
rem "path\"  ->  escaped-quote  bug when the arg is passed to cmake.
set "SRC_DIR=%~dp0."
set "BUILD_DIR=%~dp0build"
set "OUT_ROOT=%~dp0..\Assets\Plugins\Android\libs"

for %%A in (arm64-v8a armeabi-v7a) do (
    echo.
    echo === Building %%A ===
    set "ABI_BUILD=%BUILD_DIR%\%%A"
    if not exist "!ABI_BUILD!" mkdir "!ABI_BUILD!"

    cmake -S "%SRC_DIR%" -B "!ABI_BUILD!" -G Ninja ^
        "-DCMAKE_TOOLCHAIN_FILE=%ANDROID_NDK_HOME%\build\cmake\android.toolchain.cmake" ^
        -DANDROID_ABI=%%A ^
        -DANDROID_PLATFORM=android-24 ^
        -DCMAKE_BUILD_TYPE=Release ^
        "-DUNITY_PLUGIN_API_DIR=%UNITY_EDITOR_ROOT%\Editor\Data\PluginAPI"
    if errorlevel 1 exit /b 1

    cmake --build "!ABI_BUILD!" --config Release
    if errorlevel 1 exit /b 1

    if not exist "%OUT_ROOT%\%%A" mkdir "%OUT_ROOT%\%%A"
    copy /y "!ABI_BUILD!\liboes_renderer.so" "%OUT_ROOT%\%%A\liboes_renderer.so"
)

echo.
echo [build.bat] Done. .so files copied into %OUT_ROOT%
endlocal
