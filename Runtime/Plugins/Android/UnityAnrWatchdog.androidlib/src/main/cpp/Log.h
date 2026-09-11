#pragma once

#include <android/log.h>

// The engine's printf_console/ErrorStringMsg are not available to a package, so log through
// liblog under the "Unity" tag to stay consistent with the rest of the player output.
#define ANR_LOG_TAG "Unity"
#define ANR_LOG_INFO(...) __android_log_print(ANDROID_LOG_INFO, ANR_LOG_TAG, __VA_ARGS__)
#define ANR_LOG_ERROR(...) __android_log_print(ANDROID_LOG_ERROR, ANR_LOG_TAG, __VA_ARGS__)
