cmake_minimum_required(VERSION 3.20)
project(DtsCore LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS OFF)

set(DTS_SDK_ROOT "D:/WorkSpace/DTS/DTS SDK V1.0 20250625" CACHE PATH "Path to DTS SDK root folder")

add_library(dts_core SHARED
    src/dts_core.cpp
)

target_compile_definitions(dts_core PRIVATE DTS_CORE_EXPORTS)

target_include_directories(dts_core
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
        ${DTS_SDK_ROOT}
)

target_link_libraries(dts_core PRIVATE "${DTS_SDK_ROOT}/RC_FBGSystem.lib")

if (EXISTS "${DTS_SDK_ROOT}/RC_FBGSystem.dll")
    add_custom_command(TARGET dts_core POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${DTS_SDK_ROOT}/RC_FBGSystem.dll"
            "$<TARGET_FILE_DIR:dts_core>/RC_FBGSystem.dll"
    )
endif()

if (EXISTS "${DTS_SDK_ROOT}/opencv_world3412.dll")
    add_custom_command(TARGET dts_core POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${DTS_SDK_ROOT}/opencv_world3412.dll"
            "$<TARGET_FILE_DIR:dts_core>/opencv_world3412.dll"
    )
endif()
