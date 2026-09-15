cmake_minimum_required(VERSION 3.22.1)

project(UnityAnrWatchdog CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(UnityAnrWatchdog SHARED
    AnrWatchdogJni.cpp
    Modules.cpp
    NativeThreads.cpp
    ProcUtils.cpp)

# Stacks are captured by walking the frame pointer chain, so frame pointers have to be there.
target_compile_options(UnityAnrWatchdog PRIVATE
    -Wall
    -Wextra
    -fno-omit-frame-pointer)

target_link_libraries(UnityAnrWatchdog log dl)

# Android 15 and later can run with 16 KB memory pages, and a library whose LOAD segments are
# aligned to the older 4 KB page size fails to load there. NDK r27 does this by default, older
# ones do not, so the alignment is requested explicitly for every ABI.
target_link_options(UnityAnrWatchdog PRIVATE
    "-Wl,-z,max-page-size=16384"
    "-Wl,-z,common-page-size=16384")
