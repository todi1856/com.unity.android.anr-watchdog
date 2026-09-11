using System.Collections;
using System.Collections.Generic;
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

        // Reports are no longer removed when read, so the poller remembers what it has already
        // raised instead of relying on the file disappearing.
        readonly HashSet<string> m_Raised = new HashSet<string>();

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

                foreach (var report in AnrWatchdog.GetReports())
                {
                    if (m_Raised.Add(report.sourcePath))
                        AnrWatchdog.RaiseAnrDetected(report);
                }
            }
        }
    }
}
