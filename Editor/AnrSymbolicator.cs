using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Unity.Android
{
    /// <summary>
    /// Turns the addresses in a report's native stacks into function names, files and line numbers
    /// using llvm-symbolizer and the unstripped libraries from the build's symbols.zip.
    /// </summary>
    class AnrSymbolicator
    {
        public struct Symbol
        {
            public string function;

            /// <summary>Source location as file:line, empty when the symbol file has no line info.</summary>
            public string source;

            /// <summary>How many further frames were inlined into this one.</summary>
            public int inlinedFrames;

            /// <summary>
            /// True when the name came from the symbol table rather than debug info: the nearest
            /// preceding function plus an offset, with no source location and no inlined frames.
            /// </summary>
            public bool fromSymbolTable;
        }

        public struct Summary
        {
            /// <summary>Distinct library/address pairs in the report.</summary>
            public int total;

            public int resolved;
            public List<string> messages;
        }

        static readonly Regex k_BuildId = new Regex(@"Build ID:\s*(?<id>[0-9a-fA-F]+)");

        readonly Dictionary<string, Symbol> m_Symbols = new Dictionary<string, Symbol>();

        public bool TryGetSymbol(string libraryName, long address, out Symbol symbol) =>
            m_Symbols.TryGetValue(Key(libraryName, address), out symbol);

        public void Clear() => m_Symbols.Clear();

        public Summary Resolve(AnrReport report, string symbolsRoot)
        {
            var summary = new Summary { messages = new List<string>() };

            var addressesByLibrary = CollectAddresses(report);
            foreach (var library in addressesByLibrary)
                summary.total += library.Value.Count;

            if (summary.total == 0)
            {
                summary.messages.Add("The report contains no native frames to resolve.");
                return summary;
            }

            var ndkRoot = AndroidToolchain.FindNdkRoot();
            var symbolizer = AndroidToolchain.FindLlvmTool(ndkRoot, "llvm-symbolizer");
            if (symbolizer == null)
            {
                summary.messages.Add("llvm-symbolizer was not found. Set the NDK in Preferences > External Tools, or install the Android module.");
                return summary;
            }

            if (string.IsNullOrEmpty(symbolsRoot) || !Directory.Exists(symbolsRoot))
            {
                summary.messages.Add("Pick the folder holding the unstripped libraries - the contents of the build's symbols.zip.");
                return summary;
            }

            var symbolFiles = IndexSymbolFiles(symbolsRoot, report.abi);

            foreach (var library in addressesByLibrary)
            {
                if (!symbolFiles.TryGetValue(library.Key, out var symbolFile))
                {
                    summary.messages.Add($"{library.Key}: no matching symbol file under the symbols folder.");
                    continue;
                }

                VerifyBuildId(report, library.Key, symbolFile, ndkRoot, summary.messages);

                // One process per library with every address on stdin - a call per frame turns a
                // report with a thousand frames into a thousand process launches.
                // Only options that have been stable across NDK versions: an option this build of
                // llvm-symbolizer rejects makes it exit without output, which looks exactly like a
                // library with no debug info.
                var output = RunProcess(symbolizer,
                    $"--obj=\"{symbolFile}\" --functions=short --demangle --inlining=true --output-style=LLVM",
                    BuildAddressList(library.Value));

                var fromDebugInfo = output != null ? Parse(output, library.Key, library.Value) : 0;
                summary.resolved += fromDebugInfo;

                if (fromDebugInfo == 0)
                    summary.messages.Add($"{library.Key}: llvm-symbolizer resolved nothing - check the console for its error output.");

                // Libraries stripped of DWARF still carry a symbol table - libunity.so is the
                // usual case - so anything the symbolizer left unresolved gets a second pass
                // straight against the symbol table.
                var fromTable = ResolveFromSymbolTable(ndkRoot, symbolFile, library.Key, library.Value);
                summary.resolved += fromTable;
                if (fromTable > 0)
                    summary.messages.Add($"{library.Key}: {fromTable} addresses named from the symbol table only, without source lines.");
            }

            return summary;
        }

        static string Key(string libraryName, long address) =>
            $"{Path.GetFileName(libraryName ?? string.Empty)}|{address:x}";

        static Dictionary<string, List<long>> CollectAddresses(AnrReport report)
        {
            var addresses = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>();

            foreach (var thread in report.nativeThreads ?? Array.Empty<AnrReport.NativeThread>())
            {
                foreach (var frame in thread.stackTrace ?? Array.Empty<AnrReport.NativeStackFrame>())
                {
                    if (string.IsNullOrEmpty(frame.libraryName))
                        continue;

                    var library = Path.GetFileName(frame.libraryName);
                    if (!seen.Add(Key(library, frame.address)))
                        continue;

                    if (!addresses.TryGetValue(library, out var list))
                        addresses[library] = list = new List<long>();
                    list.Add(frame.address);
                }
            }

            return addresses;
        }

        static string BuildAddressList(List<long> addresses)
        {
            var input = new StringBuilder(addresses.Count * 20);
            foreach (var address in addresses)
                input.Append("0x").Append(address.ToString("x")).Append('\n');
            return input.ToString();
        }

        /// <summary>
        /// llvm-symbolizer answers each address with one function/source pair per inlined frame,
        /// innermost first, and a blank line between addresses.
        /// </summary>
        int Parse(string output, string library, List<long> addresses)
        {
            var lines = output.Replace("\r\n", "\n").Split('\n');
            var resolved = 0;
            var line = 0;

            for (var i = 0; i < addresses.Count; i++)
            {
                string function = null;
                string source = null;
                var frames = 0;

                while (line < lines.Length && lines[line].Length > 0)
                {
                    var functionLine = lines[line++];
                    var sourceLine = line < lines.Length ? lines[line++] : string.Empty;

                    if (function == null)
                    {
                        function = functionLine;
                        source = sourceLine;
                    }
                    frames++;
                }
                line++; // The blank separator.

                if (string.IsNullOrEmpty(function) || function == "??")
                    continue;

                m_Symbols[Key(library, addresses[i])] = new Symbol
                {
                    function = function,
                    source = source == "??:0" || source == "??:?" ? string.Empty : source,
                    inlinedFrames = Math.Max(0, frames - 1)
                };
                resolved++;
            }

            return resolved;
        }

        readonly struct TextSymbol
        {
            public readonly long address;
            public readonly long size;
            public readonly string name;

            public TextSymbol(long address, long size, string name)
            {
                this.address = address;
                this.size = size;
                this.name = name;
            }
        }

        // Address, size, type, name - llvm-nm --print-size --numeric-sort output. Only function
        // symbols are of interest: T/t for text, W/w for weak definitions.
        static readonly Regex k_NmLine = new Regex(@"^(?<address>[0-9a-fA-F]+)\s+(?<size>[0-9a-fA-F]+)\s+(?<type>[TtWw])\s+(?<name>.+)$");

        /// <summary>
        /// Names addresses the symbolizer could not resolve by looking them up in the library's
        /// symbol table. This is what a library with a .symtab but no DWARF can give: the function
        /// an address falls inside, and how far into it - no file, no line, no inlined frames.
        /// </summary>
        int ResolveFromSymbolTable(string ndkRoot, string symbolFile, string library, List<long> addresses)
        {
            var unresolved = addresses.Where(address => !m_Symbols.ContainsKey(Key(library, address))).ToList();
            if (unresolved.Count == 0)
                return 0;

            var nm = AndroidToolchain.FindLlvmTool(ndkRoot, "llvm-nm");
            if (nm == null)
                return 0;

            var output = RunProcess(nm, $"--defined-only --demangle --numeric-sort --print-size \"{symbolFile}\"");
            if (string.IsNullOrEmpty(output))
                return 0;

            var symbols = ParseSymbolTable(output);
            if (symbols.Count == 0)
                return 0;

            var resolved = 0;
            foreach (var address in unresolved)
            {
                var index = FindContainingSymbol(symbols, address);
                if (index < 0)
                    continue;

                var symbol = symbols[index];
                var offset = address - symbol.address;

                // Past the end of the function means the address sits in padding or in a function
                // the table does not cover - a wrong name is worse than no name.
                if (symbol.size > 0 && offset >= symbol.size)
                    continue;

                m_Symbols[Key(library, address)] = new Symbol
                {
                    function = offset == 0 ? symbol.name : $"{symbol.name} +0x{offset:x}",
                    source = string.Empty,
                    fromSymbolTable = true
                };
                resolved++;
            }

            return resolved;
        }

        static List<TextSymbol> ParseSymbolTable(string output)
        {
            var symbols = new List<TextSymbol>();

            foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
            {
                var match = k_NmLine.Match(line);
                if (!match.Success)
                    continue;

                if (!long.TryParse(match.Groups["address"].Value, System.Globalization.NumberStyles.HexNumber, null, out var address))
                    continue;
                long.TryParse(match.Groups["size"].Value, System.Globalization.NumberStyles.HexNumber, null, out var size);

                symbols.Add(new TextSymbol(address, size, match.Groups["name"].Value));
            }

            // --numeric-sort already orders these, but the parse must not depend on it.
            symbols.Sort((left, right) => left.address.CompareTo(right.address));
            return symbols;
        }

        /// <summary>Index of the last symbol starting at or before the address, or -1.</summary>
        static int FindContainingSymbol(List<TextSymbol> symbols, long address)
        {
            var low = 0;
            var high = symbols.Count - 1;
            var found = -1;

            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (symbols[middle].address <= address)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }

        /// <summary>
        /// symbols.zip ships libunity.sym.so and friends; other pipelines produce .so.debug or a
        /// plain .so. All of them are indexed under the runtime name the report refers to.
        /// </summary>
        static Dictionary<string, string> IndexSymbolFiles(string root, string abi)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = NormalizeLibraryName(Path.GetFileName(file));
                if (name == null)
                    continue;

                // symbols.zip keeps a folder per abi, so prefer the one the report came from.
                var matchesReportAbi = !string.IsNullOrEmpty(abi) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), abi, StringComparison.OrdinalIgnoreCase);

                if (matchesReportAbi || !index.ContainsKey(name))
                    index[name] = file;
            }

            return index;
        }

        static string NormalizeLibraryName(string fileName)
        {
            // ".so" has to be tested last, otherwise "libunity.sym.so" normalizes to itself.
            foreach (var suffix in new[] { ".sym.so", ".dbg.so", ".so.sym", ".so.debug", ".so" })
            {
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return fileName.Substring(0, fileName.Length - suffix.Length) + ".so";
            }

            return null;
        }

        /// <summary>
        /// The report carries the build id of every library it sampled, which is the only reliable
        /// way to tell that a symbol file belongs to a different build - addresses from the wrong
        /// binary resolve to plausible but wrong functions.
        /// </summary>
        static void VerifyBuildId(AnrReport report, string library, string symbolFile, string ndkRoot, List<string> messages)
        {
            var reported = FindReportedBuildId(report, library);
            if (string.IsNullOrEmpty(reported))
                return;

            var readelf = AndroidToolchain.FindLlvmTool(ndkRoot, "llvm-readelf");
            if (readelf == null)
                return;

            var output = RunProcess(readelf, $"--notes \"{symbolFile}\"");
            if (string.IsNullOrEmpty(output))
                return;

            var match = k_BuildId.Match(output);
            if (!match.Success)
                return;

            if (!string.Equals(match.Groups["id"].Value, reported, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"{library}: build id mismatch - '{Path.GetFileName(symbolFile)}' is from a different build, " +
                             "so the resolved names will be wrong.");
            }
        }

        static string FindReportedBuildId(AnrReport report, string library)
        {
            foreach (var thread in report.nativeThreads ?? Array.Empty<AnrReport.NativeThread>())
            {
                foreach (var frame in thread.stackTrace ?? Array.Empty<AnrReport.NativeStackFrame>())
                {
                    if (string.IsNullOrEmpty(frame.buildId) || string.IsNullOrEmpty(frame.libraryName))
                        continue;

                    if (string.Equals(Path.GetFileName(frame.libraryName), library, StringComparison.OrdinalIgnoreCase))
                        return frame.buildId;
                }
            }

            return null;
        }

        static string RunProcess(string fileName, string arguments, string standardInput = null)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = fileName;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.RedirectStandardInput = standardInput != null;

                    process.Start();

                    if (standardInput != null)
                    {
                        process.StandardInput.Write(standardInput);
                        process.StandardInput.Close();
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(error))
                        UnityEngine.Debug.LogWarning($"{Path.GetFileName(fileName)}: {error.Trim()}");

                    return output;
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError($"Failed to run '{fileName}': {exception.Message}");
                return null;
            }
        }
    }
}
