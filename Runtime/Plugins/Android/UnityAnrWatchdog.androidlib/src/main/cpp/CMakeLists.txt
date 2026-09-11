cmake_minimum_required(VERSION 3.22.1)

project(UnityAnrWatchdog CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(UnityAnrWatchdog SHARED
    AnrWatchdogJni.cpp
    NativeThreads.cpp
    ProcUtils.cpp)

# Unwind tables are what _Unwind_Backtrace walks - without them the captured stacks are empty or
# one frame deep on release builds.
target_compile_options(UnityAnrWatchdog PRIVATE
    -Wall
    -Wextra
    -funwind-tables
    -fno-omit-frame-pointer)

target_link_libraries(UnityAnrWatchdog log dl)
