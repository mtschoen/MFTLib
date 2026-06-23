cmake_minimum_required(VERSION 3.20)
project(MFTLibNative CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

option(MFTLIB_ENABLE_COVERAGE "Enable gcov/gcovr coverage instrumentation (POSIX only)" OFF)

if(MFTLIB_ENABLE_COVERAGE AND UNIX)
    add_compile_options(--coverage -O0 -g)
    add_link_options(--coverage)
endif()

set(SOURCES
    core/test_hooks.cpp
    mft/mft.cpp
    mft/mft_synthetic.cpp
)

if(WIN32)
    list(APPEND SOURCES
        dllmain.cpp
        usn/usn_journal.cpp
        core/platform_win32.cpp
    )
else()
    list(APPEND SOURCES
        core/platform_posix.cpp
    )
endif()

add_library(MFTLibNative SHARED ${SOURCES})

target_include_directories(MFTLibNative PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/core
)

if(UNIX)
    target_compile_options(MFTLibNative PRIVATE
        -Wall -Wextra -Wno-unused-parameter
    )
    target_link_libraries(MFTLibNative PRIVATE pthread)
endif()

# Smoke test (Linux only for MVP)
if(UNIX AND BUILD_TESTING)
    add_subdirectory(test)
endif()

# CLI tool (Linux only for MVP)
if(UNIX)
    add_subdirectory(tools)
endif()
