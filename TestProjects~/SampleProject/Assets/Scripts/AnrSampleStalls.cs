using System;
using System.Threading;
using UnityEngine;

/// <summary>
/// The two ways of making this app stop responding. They are not equivalent: Android watches its
/// own UI thread, which on Android players is a different thread from the one the Unity player
/// loop runs on, so only the first of these produces a real ANR.
/// </summary>
static class AnrSampleStalls
{
    /// <summary>
    /// Blocks the Android UI thread, which is what Android - and this package's watchdog - watch.
    /// Returns immediately; the stall happens on the UI thread.
    /// </summary>
    public static bool StallAndroidUiThread(float seconds, out string error)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            if (activity == null)
            {
                error = "No current activity";
                return false;
            }

            activity.Call("runOnUiThread", new AndroidJavaRunnable(() => Sleep(seconds)));
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
#else
        error = "Only available in an Android player";
        return false;
#endif
    }

    /// <summary>
    /// Blocks the Unity player loop. Rendering and scripts freeze, but the Android UI thread keeps
    /// servicing its message queue, so no ANR is raised.
    /// </summary>
    public static void StallUnityMainThread(float seconds) => Sleep(seconds);

    static void Sleep(float seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));
}
