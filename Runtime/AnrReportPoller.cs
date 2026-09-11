using System.Collections;
using UnityEngine;

namespace Unity.Android
{
    /// <summary>
    /// Picks up reports the watchdog wrote while the main thread was stuck and raises
    /// <see cref="AnrWatchdog.AnrDetected"/> for them once it is running again.
    /// </summary>
    sealed class AnrReportPoller : MonoBehaviour
    {
        static AnrReportPoller s_Instance;

        float m_IntervalSeconds;

        internal static void Run(float intervalSeconds)
        {
            if (s_Instance != null)
            {
                s_Instance.m_IntervalSeconds = intervalSeconds;
                return;
            }

            var host = new GameObject(nameof(AnrReportPoller)) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);

            s_Instance = host.AddComponent<AnrReportPoller>();
            s_Instance.m_IntervalSeconds = intervalSeconds;
        }

        internal static void Shutdown()
        {
            if (s_Instance == null)
                return;

            Destroy(s_Instance.gameObject);
            s_Instance = null;
        }

        IEnumerator Start()
        {
            while (true)
            {
                // Unscaled, so a stalled or paused game does not change how often reports surface.
                yield return new WaitForSecondsRealtime(m_IntervalSeconds);

                foreach (var report in AnrWatchdog.TakePendingReports())
                    AnrWatchdog.RaiseAnrDetected(report);
            }
        }
    }
}
