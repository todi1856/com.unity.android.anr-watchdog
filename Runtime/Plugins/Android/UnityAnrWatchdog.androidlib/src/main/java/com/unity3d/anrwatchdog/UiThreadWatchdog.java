package com.unity3d.anrwatchdog;

import android.app.Activity;
import android.content.Context;
import android.content.res.Configuration;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.text.SimpleDateFormat;
import java.time.ZoneOffset;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.Date;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TimeZone;

/**
 * Watches the Android UI thread - the activity's Looper thread, which is what Android itself
 * watches - from a background thread. Note this is not the Unity main thread: the player loop runs
 * on a thread of its own and keeps going while the UI thread is stuck.
 * <p>
 * The UI thread is considered unresponsive when a runnable posted to its Looper has not run for
 * longer than the configured timeout. At that point the Java thread dump is collected here and
 * handed to native code, which appends the native thread dump and writes the merged report to disk.
 */
class UiThreadWatchdog extends Thread
{
    static
    {
        System.loadLibrary("UnityAnrWatchdog");
    }

    private long m_ANRLastReportedMs;
    private long m_ANRReportIntervalMs;
    private long m_ANRTimeoutMs;
    private long m_PollIntervalMs;
    private volatile long m_LastTickTimeMs = 0;
    private volatile boolean m_NeedsPosting;

    private final Handler m_UIHandler = new Handler(Looper.getMainLooper());
    private final Runnable m_Ticker = new Runnable() {
        @Override public void run() {
            m_LastTickTimeMs = System.currentTimeMillis();
            m_NeedsPosting = true;
        }
    };

    private final Context m_Context;
    private final Activity m_Activity;
    private final File m_ReportDirectory;

    // Metadata the engine knows about but this module cannot query itself - supplied from C#.
    private String m_UnityVersion = "";
    private String m_ScriptingBackend = "";
    private String m_BuildType = "";

    private boolean m_WorldReadableReports = true;

    UiThreadWatchdog(Context context, Activity activity, File reportDirectory) {
        m_Context = context;
        m_Activity = activity;
        m_ReportDirectory = reportDirectory;
        m_ANRReportIntervalMs = 10000;
        m_PollIntervalMs = 300;
        m_ANRTimeoutMs = 3000;
    }

    /**
     * Set the minimum interval between ANR reports.
     * Setting this value to zero will cause ANR to be reported each tick.
     */
    void setANRReportInterval(long milliseconds) {
        if (milliseconds < 0)
            throw new IllegalArgumentException(String.format(Locale.ROOT, "Value must be bigger than zero. Your value %d", milliseconds));
        m_ANRReportIntervalMs = milliseconds;
    }

    /**
     * For how long the Android UI thread should be stuck, so we could consider this is an ANR.
     */
    void setANRTimeout(long milliseconds) {
        if (milliseconds < 0)
            throw new IllegalArgumentException(String.format(Locale.ROOT, "Value must be bigger than zero. Your value %d", milliseconds));
        m_ANRTimeoutMs = milliseconds;
    }

    /**
     * How often to check for ANR?
     * Setting value to 0, will perform check each frame and might cause performance issues.
     */
    void setANRPollInterval(long milliseconds) {
        if (milliseconds < 0)
            throw new IllegalArgumentException(String.format(Locale.ROOT, "Value must be bigger than zero. Your value %d", milliseconds));
        m_PollIntervalMs = milliseconds;
    }

    /**
     * Whether reports are written as 0644 rather than owner-only 0600.
     */
    void setWorldReadableReports(boolean worldReadable) {
        m_WorldReadableReports = worldReadable;
    }

    void setEngineMetadata(String unityVersion, String scriptingBackend, String buildType) {
        m_UnityVersion = unityVersion == null ? "" : unityVersion;
        m_ScriptingBackend = scriptingBackend == null ? "" : scriptingBackend;
        m_BuildType = buildType == null ? "" : buildType;
    }

    @Override
    public void run() {
        setName(getClass().getName());

        m_LastTickTimeMs = System.currentTimeMillis();
        m_NeedsPosting = true;
        while (!isInterrupted()) {

            if (m_NeedsPosting) {
                m_NeedsPosting = false;
                m_UIHandler.post(m_Ticker);
            }

            // TODO: respect debugger

            long currentTime = System.currentTimeMillis();
            long currentTimeout = currentTime - m_LastTickTimeMs;
            if (currentTimeout > m_ANRTimeoutMs) {

                // Prevent ANR being reported each tick
                if (currentTime - m_ANRLastReportedMs > m_ANRReportIntervalMs) {
                    m_ANRLastReportedMs = currentTime;
                    onApplicationNotResponding(currentTimeout);
                }
            }

            try {
                Thread.sleep(m_PollIntervalMs);
            } catch (InterruptedException e) {
                logMessage("UiThreadWatchdog was interrupted");
                return;
            }
        }
    }

    private void onApplicationNotResponding(long anrTimeMs) {
        logMessage("ANR detected, the Android UI thread has been unresponsive for " + anrTimeMs + " ms");

        try {
            String javaReport = toJson(Thread.getAllStackTraces().entrySet(), anrTimeMs).toString(4);
            String reportPath = createReportPath();
            if (reportPath == null)
                return;

            // Native side appends the native thread dump and writes the merged report atomically.
            if (!nativeApplicationNotResponding(javaReport, reportPath, m_WorldReadableReports))
                logMessage("Failed to write ANR report to " + reportPath);
        } catch (JSONException e) {
            logMessage("Failed to serialize ANR report: " + e);
        }
    }

    private String createReportPath() {
        if (!m_ReportDirectory.exists() && !m_ReportDirectory.mkdirs()) {
            logMessage("Failed to create ANR report directory " + m_ReportDirectory);
            return null;
        }

        SimpleDateFormat nameFormat = new SimpleDateFormat("yyyyMMdd-HHmmss-SSS", Locale.ROOT);
        nameFormat.setTimeZone(TimeZone.getTimeZone("UTC"));
        return new File(m_ReportDirectory, "anr-" + nameFormat.format(new Date()) + ".json").getAbsolutePath();
    }

    private static void logMessage(String message) {
        android.util.Log.v("Unity", message);
    }

    private static String getCurrentDateTimeUTC() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            ZonedDateTime utcDateTime = ZonedDateTime.now().withZoneSameInstant(ZoneOffset.UTC);
            DateTimeFormatter formatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss 'UTC'");
            return formatter.format(utcDateTime);
        }
        Date now = new Date();
        SimpleDateFormat sdf = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss 'UTC'", Locale.ROOT);
        sdf.setTimeZone(TimeZone.getTimeZone("UTC"));
        return sdf.format(now);
    }

    private JSONObject toJson(Set<Map.Entry<Thread, StackTraceElement[]>> threads, long anrTimeMs) throws JSONException {
        JSONObject jsonObject = new JSONObject();
        JSONArray jsonThreads = new JSONArray();
        for (Map.Entry<Thread, StackTraceElement[]> threadItem : threads) {
            JSONObject jsonThread = new JSONObject();
            Thread thread = threadItem.getKey();
            jsonThread.put("name", thread.getName());
            jsonThread.put("id", thread.getId());
            jsonThread.put("state", thread.getState());
            jsonThread.put("priority", thread.getPriority());
            jsonThread.put("stackTrace", getStackTraceJson(threadItem.getValue()));
            jsonThreads.put(jsonThread);
        }

        jsonObject.put("reportType", "ANR");
        jsonObject.put("reportTimeStamp", getCurrentDateTimeUTC());
        jsonObject.put("anrTimeMs", anrTimeMs);
        jsonObject.put("packageName", m_Context.getPackageName());
        jsonObject.put("contextClassName", m_Context.getClass().getSimpleName());
        jsonObject.put("unityVersion", m_UnityVersion);
        jsonObject.put("deviceModel", String.format(Locale.ROOT, "%s %s", Build.MANUFACTURER, Build.MODEL));
        jsonObject.put("deviceFingerPrint", Build.FINGERPRINT);
        jsonObject.put("deviceApiLevel", Build.VERSION.SDK_INT);
        jsonObject.put("buildType", m_BuildType);
        jsonObject.put("scriptingBackend", m_ScriptingBackend);
        jsonObject.put("multiWindow", isInMultiWindowMode());
        jsonObject.put("orientation", getOrientation());

        jsonObject.put("javaThreads", jsonThreads);
        // TODO:
        // app version
        // Foreground
        // Window size ?
        // State from the game - loading, playing
        return jsonObject;
    }

    private static JSONArray getStackTraceJson(StackTraceElement[] stackTrace) throws JSONException {
        JSONArray jsonStacktrace = new JSONArray();
        for (StackTraceElement element : stackTrace) {
            JSONObject jsonStacktraceFrame = new JSONObject();
            jsonStacktraceFrame.put("className", element.getClassName());
            jsonStacktraceFrame.put("methodName", element.getMethodName());
            jsonStacktraceFrame.put("fileName", element.getFileName());
            jsonStacktraceFrame.put("lineNumber", element.getLineNumber());

            jsonStacktrace.put(jsonStacktraceFrame);
        }
        return jsonStacktrace;
    }

    private boolean isInMultiWindowMode() {
        if (m_Activity == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.N)
            return false;
        return m_Activity.isInMultiWindowMode();
    }

    private String getOrientation() {
        int orientation = m_Context.getResources().getConfiguration().orientation;
        switch (orientation)
        {
            case Configuration.ORIENTATION_PORTRAIT: return "Portrait";
            case Configuration.ORIENTATION_LANDSCAPE: return "Landscape";
            default: return "Unknown";
        }
    }

    /**
     * Collects the native thread dump, merges it with the supplied Java report and writes the
     * result to reportPath. Called on the watchdog thread while the Android UI thread is stuck.
     */
    private native boolean nativeApplicationNotResponding(String javaThreadsJson, String reportPath, boolean worldReadable);
}
