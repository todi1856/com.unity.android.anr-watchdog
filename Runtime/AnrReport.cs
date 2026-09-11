using System;

namespace Unity.Android
{
    /// <summary>
    /// A single ANR report, as written by the watchdog to <see cref="AnrWatchdog.ReportDirectory"/>.
    /// The Java half is collected while the main thread is stuck; the native half is collected by
    /// signalling every thread of the process and unwinding it.
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

        /// <summary>How long the main thread had been unresponsive when the report was taken.</summary>
        public long anrTimeMs;

        public string packageName;
        public string contextClassName;
        public string unityVersion;
        public string deviceModel;
        public string deviceFingerPrint;
        public int deviceApiLevel;
        public string buildType;
        public string scriptingBackend;
        public bool multiWindow;
        public string orientation;

        public string abi;
        public int processId;
        public int userId;

        public JavaThread[] javaThreads;
        public NativeThread[] nativeThreads;
    }
}
