cmake_minimum_required(VERSION 3.18)
project(oes_renderer C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_VISIBILITY_PRESET hidden)

# Unity plugin headers live inside the editor install.
# Pass -DUNITY_PLUGIN_API_DIR=<path> on cmake command line, or set
# the UNITY_EDITOR_ROOT env var (Windows installs default below).
if(NOT DEFINED UNITY_PLUGIN_API_DIR)
    if(DEFINED ENV{UNITY_EDITOR_ROOT})
        set(UNITY_PLUGIN_API_DIR
            "$ENV{UNITY_EDITOR_ROOT}/Editor/Data/PluginAPI")
    else()
        # sensible default for current editor in this project
        set(UNITY_PLUGIN_API_DIR
            "C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor/Data/PluginAPI")
    endif()
endif()

if(NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityInterface.h")
    message(FATAL_ERROR
        "Unity PluginAPI headers not found at: ${UNITY_PLUGIN_API_DIR}\n"
        "Set -DUNITY_PLUGIN_API_DIR=<...>/Editor/Data/PluginAPI or UNITY_EDITOR_ROOT env var.")
endif()

add_library(oes_renderer SHARED oes_renderer.c)

target_include_directories(oes_renderer PRIVATE "${UNITY_PLUGIN_API_DIR}")

target_compile_options(oes_renderer PRIVATE
    -Wall -Wextra -Wno-unused-parameter -O2 -fvisibility=hidden)

target_link_libraries(oes_renderer
    GLESv2
    log
    android)
