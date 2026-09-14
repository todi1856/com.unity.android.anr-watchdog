# The native library binds nativeApplicationNotResponding by name through RegisterNatives, and
# UnityAnrWatchdog is reached by reflection from C#. Neither may be renamed or stripped.
-keep class com.unity3d.anrwatchdog.UnityAnrWatchdog { *; }
-keepclasseswithmembers class com.unity3d.anrwatchdog.UiThreadWatchdog {
    native <methods>;
}
