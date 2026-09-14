using System;

namespace Unity.Android
{
    /// <summary>
    /// A single ANR report, as written by the watchdog to <see cref="AnrWatchdog.ReportDirectory"/>.
    /// The Java half is collected while the Android UI thread is stuck; the native half is
    /// collected by signalling every thread of the process and unwinding it.
    /// </summary>
    /// <remarks>
    /// Field names intentionally match the on-disk JSON so <see cref="UnityEngine.JsonUtility"/>
    /// can map them without an intermediate representation.
    /// </remarks>
    [Serializable]
    public class AnrReport
    {
        [Serializable]
        public class JavaStackFrame
        {
            public string className;
            public string methodName;
            public string fileName;

            /// <summary>Negative for native (JNI) frames, matching StackTraceElement.</summary>
            public int lineNumber;
        }

        [Serializable]
        public class JavaThread
        {
            public string name;
            public long id;
            public string state;
            public int priority;
            public JavaStackFrame[] stackTrace;
        }

        [Serializable]
        public class NativeStackFrame
        {
            /// <summary>
            /// Address relative to the load address of <see cref="libraryName"/>, ready to be
            /// symbolicated offline against the unstripped binary from symbols.zip. Absolute when
            /// the address maps to no known library.
            /// </summary>
            public long address;

            public string libraryName;

            /// <summary>
            /// GNU build id of <see cref="libraryName"/> as hex. Identifies the exact binary the
            /// address came from, so a symbol server can pick the matching unstripped library.
            /// Empty when the library carries no build id note.
            /// </summary>
            public string buildId;
        }

        [Serializable]
        public class NativeThread
        {
            public string name;
            public int id;

            /// <summary>Scheduler state from /proc/&lt;tid&gt;/status, for example "R (running)".</summary>
            public string state;

            public int priority;
            public NativeStackFrame[] stackTrace;
        }

        public string reportType;
        public string reportTimeStamp;

        /// <summary>
        /// How long the Android UI thread had been unresponsive when the report was taken.
        /// </summary>
        public long anrTimeMs;

        public string packageName;
        /// <summary>
        /// Activity the app was running, as Activity.getLocalClassName() reports it - the entry
        /// point the report was taken from.
        /// </summary>
        public string entry;
        public string unityVersion;
        public string deviceModel;
        public string deviceFingerPrint;
        public int deviceApiLevel;
        public string buildType;
        public string scriptingBackend;

        /// <summary>versionName from the app's manifest.</summary>
        public string appVersion;

        /// <summary>versionCode from the app's manifest.</summary>
        public long appVersionCode;

        /// <summary>
        /// Whether the user was looking at the app when it stopped responding. A background ANR is
        /// a different problem, and Android treats it differently too.
        /// </summary>
        public bool foreground;

        /// <summary>Raw ActivityManager.RunningAppProcessInfo importance behind <see cref="foreground"/>.</summary>
        public int processImportance;

        public bool multiWindow;
        public string orientation;

        public int windowWidthPx;
        public int windowHeightPx;
        public int windowWidthDp;
        public int windowHeightDp;
        public int densityDpi;

        /// <summary>
        /// Whatever the game last set through <see cref="AnrWatchdog.GameState"/> - "loading",
        /// "menu", "level 3". Empty when the game never set one.
        /// </summary>
        public string gameState;

        public string abi;
        public int processId;
        public int userId;

        public JavaThread[] javaThreads;
        public NativeThread[] nativeThreads;

        /// <summary>
        /// File this report was read from. Set by <see cref="AnrWatchdog.GetReports"/>, never part
        /// of the report on disk - reports stay on disk until cleared, so callers need a way to
        /// tell which ones they have already seen.
        /// </summary>
        [NonSerialized]
        public string sourcePath;
    }
}
