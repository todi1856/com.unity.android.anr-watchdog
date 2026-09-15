using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace Unity.Android
{
    /// <summary>
    /// Unity 2021.3 does not write the unity.* properties into the generated Gradle project that
    /// later versions do, so the watchdog's .androidlib has nothing to read the NDK location from.
    /// This appends the two it needs to the project's gradle.properties, leaving alone anything
    /// that is already there - including the properties a newer Unity writes itself.
    /// </summary>
    class AnrWatchdogGradleProperties : IPostGenerateGradleAndroidProject
    {
        const string k_NdkPathProperty = "unity.androidNdkPath";
        const string k_NdkVersionProperty = "unity.androidNdkVersion";

        public int callbackOrder => 0;

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            var ndkRoot = AndroidExternalToolsSettings.ndkRootPath;
            if (string.IsNullOrEmpty(ndkRoot) || !Directory.Exists(ndkRoot))
            {
                Debug.LogWarning("ANR watchdog: no NDK is configured in Preferences > External Tools, " +
                                 $"so {k_NdkPathProperty} was not written and the plugin will build against " +
                                 "whatever NDK the generated project resolves.");
                return;
            }

            // The callback is handed the unityLibrary module; gradle.properties belongs to the
            // project that contains it.
            var projectRoot = Directory.GetParent(unityLibraryPath)?.FullName ?? unityLibraryPath;
            var propertiesPath = Path.Combine(projectRoot, "gradle.properties");

            string existing;
            try
            {
                existing = File.Exists(propertiesPath) ? File.ReadAllText(propertiesPath) : string.Empty;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"ANR watchdog: could not read '{propertiesPath}': {exception.Message}");
                return;
            }

            var added = new List<string>();

            // Forward slashes: a Windows path with backslashes would need escaping in a .properties
            // file, and Gradle understands forward slashes on every platform.
            if (!existing.Contains(k_NdkPathProperty))
                added.Add($"{k_NdkPathProperty}={ndkRoot.Replace('\\', '/')}");

            if (!existing.Contains(k_NdkVersionProperty))
            {
                var version = ReadNdkVersion(ndkRoot);
                if (!string.IsNullOrEmpty(version))
                    added.Add($"{k_NdkVersionProperty}={version}");
            }

            if (added.Count == 0)
                return;

            try
            {
                var text = existing;
                if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
                    text += Environment.NewLine;

                text += string.Join(Environment.NewLine, added) + Environment.NewLine;
                File.WriteAllText(propertiesPath, text);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"ANR watchdog: could not write '{propertiesPath}': {exception.Message}");
                return;
            }

            Debug.Log($"ANR watchdog: added {string.Join(", ", added)} to {propertiesPath}");
        }

        /// <summary>
        /// The NDK records its own revision in source.properties as "Pkg.Revision = 23.1.7779620".
        /// Falls back to the folder name, which for an SDK-managed NDK is the version.
        /// </summary>
        static string ReadNdkVersion(string ndkRoot)
        {
            var sourceProperties = Path.Combine(ndkRoot, "source.properties");

            try
            {
                if (File.Exists(sourceProperties))
                {
                    foreach (var line in File.ReadAllLines(sourceProperties))
                    {
                        var separator = line.IndexOf('=');
                        if (separator < 0)
                            continue;

                        if (line.Substring(0, separator).Trim() == "Pkg.Revision")
                            return line.Substring(separator + 1).Trim();
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"ANR watchdog: could not read '{sourceProperties}': {exception.Message}");
            }

            return new DirectoryInfo(ndkRoot).Name;
        }
    }
}
