using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace Unity.Android
{
    /// <summary>
    /// Locates the LLVM tools that ship with the Android NDK.
    /// </summary>
    static class AndroidToolchain
    {
        /// <summary>
        /// NDK the editor is configured with in Preferences > External Tools, or null when it is
        /// not set up.
        /// </summary>
        public static string FindNdkRoot()
        {
            var ndkRoot = AndroidExternalToolsSettings.ndkRootPath;
            return !string.IsNullOrEmpty(ndkRoot) && Directory.Exists(ndkRoot) ? ndkRoot : null;
        }

        /// <summary>
        /// Full path to an NDK LLVM tool such as llvm-symbolizer, or null when it is not there.
        /// The host folder under prebuilt/ is discovered rather than assumed, so this works on
        /// Windows, macOS and Linux editors alike.
        /// </summary>
        public static string FindLlvmTool(string ndkRoot, string toolName)
        {
            if (string.IsNullOrEmpty(ndkRoot))
                return null;

            var prebuilt = Path.Combine(ndkRoot, "toolchains", "llvm", "prebuilt");
            if (!Directory.Exists(prebuilt))
                return null;

            var executable = Application.platform == RuntimePlatform.WindowsEditor ? toolName + ".exe" : toolName;
            foreach (var host in Directory.GetDirectories(prebuilt))
            {
                var candidate = Path.Combine(host, "bin", executable);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
