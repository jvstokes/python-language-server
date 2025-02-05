﻿// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.Python.Core.IO {
    public static class PathUtils {
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly char[] InvalidFileNameChars;

        static PathUtils() {
            InvalidFileNameChars = Path.GetInvalidFileNameChars();
            Array.Sort(InvalidFileNameChars);
        }

        public static readonly char[] DirectorySeparators = {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        };

        public static bool IsValidWindowsDriveChar(char value)
            => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        public static bool IsValidFileNameCharacter(char character)
            => Array.BinarySearch(InvalidFileNameChars, character) < 0;

        /// <summary>
        /// Returns true if the path has a directory separator character at the end.
        /// </summary>
        public static bool HasEndSeparator(string path)
            => !string.IsNullOrEmpty(path) && IsDirectorySeparator(path[path.Length - 1]);


        public static bool IsDirectorySeparator(char c) => Array.IndexOf(DirectorySeparators, c) != -1;

        public static bool PathStartsWith(string s, string prefix)
            => s != null && s.StartsWith(prefix, StringExtensions.PathsStringComparison) &&
               (s.Length == prefix.Length || IsDirectorySeparator(s[prefix.Length]));

        /// <summary>
        /// Removes up to one directory separator character from the end of path.
        /// </summary>
        public static string TrimEndSeparator(string path) {
            if (HasEndSeparator(path)) {
                if (path.Length > 2 && path[path.Length - 2] == ':') {
                    // The slash at the end of a drive specifier is not actually
                    // a separator.
                    return path;
                }
                if (path.Length > 3 && path[path.Length - 2] == path[path.Length - 1] && path[path.Length - 3] == ':') {
                    // The double slash at the end of a schema is not actually a
                    // separator.
                    return path;
                }
                return path.Remove(path.Length - 1);
            }
            return path;
        }

        /// <summary>
        /// Adds a directory separator character to the end of path if required.
        /// </summary>
        public static string EnsureEndSeparator(string path) {
            if (string.IsNullOrEmpty(path)) {
                return string.Empty;
            }
            if (!HasEndSeparator(path)) {
                return path + Path.DirectorySeparatorChar;
            }
            return path;
        }

        /// <summary>
        /// Recursively searches for a file using breadth-first-search. This
        /// ensures that the result closest to <paramref name="root"/> is
        /// returned first.
        /// </summary>
        /// <param name="root">
        /// Directory to start searching.
        /// </param>
        /// <param name="file">
        /// Filename to find. Wildcards are not supported.
        /// </param>
        /// <param name="depthLimit">
        /// The number of subdirectories to search in.
        /// </param>
        /// <param name="firstCheck">
        /// A sequence of subdirectories to prioritize.
        /// </param>
        /// <returns>
        /// The path to the file if found, including <paramref name="root"/>;
        /// otherwise, null.
        /// </returns>
        public static string FindFile(
            string root,
            string file,
            int depthLimit = 2,
            IEnumerable<string> firstCheck = null
        ) {
            if (!Directory.Exists(root)) {
                return null;
            }

            var candidate = Path.Combine(root, file);
            if (File.Exists(candidate)) {
                return candidate;
            }
            if (firstCheck != null) {
                foreach (var subPath in firstCheck) {
                    candidate = Path.Combine(root, subPath, file);
                    if (File.Exists(candidate)) {
                        return candidate;
                    }
                }
            }

            // Do a BFS of the filesystem to ensure we find the match closest to
            // the root directory.
            var dirQueue = new Queue<KeyValuePair<string, int>>();
            dirQueue.Enqueue(new KeyValuePair<string, int>(root, 0));
            while (dirQueue.Any()) {
                var dirDepth = dirQueue.Dequeue();
                var dir = dirDepth.Key;
                var result = EnumerateFiles(dir, file, recurse: false).FirstOrDefault();
                if (result != null) {
                    return result;
                }
                var depth = dirDepth.Value;
                if (depth < depthLimit) {
                    foreach (var subDir in EnumerateDirectories(dir, recurse: false)) {
                        dirQueue.Enqueue(new KeyValuePair<string, int>(subDir, depth + 1));
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Safely enumerates all subdirectories under a given root. If a
        /// subdirectory is inaccessible, it will not be returned (compare and
        /// contrast with Directory.GetDirectories, which will crash without
        /// returning any subdirectories at all).
        /// </summary>
        /// <param name="root">
        /// Directory to enumerate under. This is not returned from this
        /// function.
        /// </param>
        /// <param name="recurse">
        /// <c>true</c> to return subdirectories of subdirectories.
        /// </param>
        /// <param name="fullPaths">
        /// <c>true</c> to return full paths for all subdirectories. Otherwise,
        /// the relative path from <paramref name="root"/> is returned.
        /// </param>
        public static IEnumerable<string> EnumerateDirectories(
            string root,
            bool recurse = true,
            bool fullPaths = true
        ) {
            var queue = new Queue<string>();
            root = EnsureEndSeparator(root);
            queue.Enqueue(root);

            while (queue.Any()) {
                var path = queue.Dequeue();
                path = EnsureEndSeparator(path);

                IEnumerable<string> dirs = null;
                try {
                    dirs = Directory.GetDirectories(path);
                } catch (UnauthorizedAccessException) {
                } catch (IOException) {
                }
                if (dirs == null) {
                    continue;
                }

                foreach (var d in dirs) {
                    if (!fullPaths && !d.StartsWithOrdinal(root, ignoreCase: true)) {
                        continue;
                    }
                    if (recurse) {
                        queue.Enqueue(d);
                    }
                    yield return fullPaths ? d : d.Substring(root.Length);
                }
            }
        }

        /// <summary>
        /// Returns the path to the parent directory segment of a path. If the
        /// last character of the path is a directory separator, the segment
        /// prior to that character is removed. Otherwise, the segment following
        /// the last directory separator is removed.
        /// </summary>
        /// <remarks>
        /// This should be used in place of:
        /// <c>Path.GetDirectoryName(CommonUtils.TrimEndSeparator(path)) + Path.DirectorySeparatorChar</c>
        /// </remarks>
        public static string GetParent(string path) {
            if (string.IsNullOrEmpty(path) || path.Length <= 1) {
                return string.Empty;
            }

            var last = path.Length - 1;
            if (DirectorySeparators.Contains(path[last])) {
                last -= 1;
            }

            if (last <= 0) {
                return string.Empty;
            }

            last = path.LastIndexOfAny(DirectorySeparators, last);

            if (last < 0) {
                return string.Empty;
            }

            return path.Remove(last + 1);
        }

        /// <summary>
        /// Safely gets the name from the specified path, even if the path
        /// contains invalid characters.
        /// </summary>
        public static string GetFileName(string filePath) {
            if (string.IsNullOrEmpty(filePath)) {
                return string.Empty;
            }

            var i = filePath.LastIndexOfAny(DirectorySeparators);
            return filePath.Substring(i + 1);
        }

        /// <summary>
        /// Safely enumerates all files under a given root. If a subdirectory is
        /// inaccessible, its files will not be returned (compare and contrast
        /// with Directory.GetFiles, which will crash without returning any
        /// files at all).
        /// </summary>
        /// <param name="root">
        /// Directory to enumerate.
        /// </param>
        /// <param name="pattern">
        /// File pattern to return. You may use wildcards * and ?.
        /// </param>
        /// <param name="recurse">
        /// <c>true</c> to return files within subdirectories.
        /// </param>
        /// <param name="fullPaths">
        /// <c>true</c> to return full paths for all subdirectories. Otherwise,
        /// the relative path from <paramref name="root"/> is returned.
        /// </param>
        public static IEnumerable<string> EnumerateFiles(
            string root,
            string pattern = "*",
            bool recurse = true,
            bool fullPaths = true
        ) {
            root = EnsureEndSeparator(root);

            var dirs = Enumerable.Repeat(root, 1);
            if (recurse) {
                dirs = dirs.Concat(EnumerateDirectories(root, true, false));
            }

            foreach (var dir in dirs) {
                var fullDir = Path.IsPathRooted(dir) ? dir : (root + dir);
                var dirPrefix = "";
                if (!Path.IsPathRooted(dir)) {
                    dirPrefix = EnsureEndSeparator(dir);
                }


                IEnumerable<string> files = null;
                try {
                    if (Directory.Exists(fullDir)) {
                        files = Directory.GetFiles(fullDir, pattern);
                    }
                } catch (UnauthorizedAccessException) {
                } catch (IOException) {
                }
                if (files == null) {
                    continue;
                }

                foreach (var f in files) {
                    if (fullPaths) {
                        yield return f;
                    } else {
                        var relPath = dirPrefix + GetFileName(f);
                        if (File.Exists(root + relPath)) {
                            yield return relPath;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Deletes a file, making multiple attempts and suppressing any
        /// IO-related errors.
        /// </summary>
        /// <param name="path">The full path of the file to delete.</param>
        /// <returns>True if the file was successfully deleted.</returns>
        public static bool DeleteFile(string path) {
            for (var retries = 5; retries > 0; --retries) {
                try {
                    if (File.Exists(path)) {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        return true;
                    }
                } catch (UnauthorizedAccessException) {
                } catch (IOException) {
                }
                Thread.Sleep(10);
            }
            return !File.Exists(path);
        }

        /// <summary>
        /// Recursively deletes a directory, making multiple attempts
        /// and suppressing any IO-related errors.
        /// </summary>
        /// <param name="path">The full path of the directory to delete.</param>
        /// <returns>True if the directory was successfully deleted.</returns>
        public static bool DeleteDirectory(string path) {
            for (var retries = 2; retries > 0; --retries) {
                try {
                    Directory.Delete(path, true);
                    return true;
                } catch (UnauthorizedAccessException) {
                } catch (IOException) {
                }
            }

            // Regular delete failed, so let's start removing the contents ourselves
            var subdirs = EnumerateDirectories(path).OrderByDescending(p => p.Length).ToArray();
            foreach (var dir in subdirs) {
                foreach (var f in EnumerateFiles(dir, recurse: false).ToArray()) {
                    DeleteFile(f);
                }

                try {
                    Directory.Delete(dir, true);
                } catch (UnauthorizedAccessException) {
                } catch (IOException) {
                }
            }

            // If we get to this point and the directory still exists, there's
            // likely nothing we can do.
            return !Directory.Exists(path);
        }

        public static FileStream OpenWithRetry(string file, FileMode mode, FileAccess access, FileShare share) {
            // Retry for up to one second
            var create = mode != FileMode.Open;
            for (var retries = 100; retries > 0; --retries) {
                try {
                    return new FileStream(file, mode, access, share);
                } catch (FileNotFoundException) when (!create) {
                    return null;
                } catch (DirectoryNotFoundException) when (!create) {
                    return null;
                } catch (UnauthorizedAccessException) {
                    Thread.Sleep(10);
                } catch (IOException) {
                    if (create) {
                        var dir = Path.GetDirectoryName(file);
                        try {
                            Directory.CreateDirectory(dir);
                        } catch (IOException) {
                            // Cannot create directory for DB, so just bail out
                            return null;
                        }
                    }
                    Thread.Sleep(10);
                } catch (NotSupportedException) {
                    return null;
                }
            }
            return null;
        }

        public static bool IsNormalizedPath(string path) {
            var separator = Path.DirectorySeparatorChar;
            var altSeparator = Path.AltDirectorySeparatorChar;
            int offset;
            if (path.Length >= 1 && path[0] == separator) {
                offset = IsUncStart(path) ? 2 : 1;
            } else if (IsWindows && path.Length >= 2 && IsValidWindowsDriveChar(path[0]) && path[1] == ':') {
                offset = 2;
            } else {
                return false;
            }

            var impossibleName = false;
            for (var i = offset; i < path.Length; i++) {
                var character = path[i];
                if (character == altSeparator && separator != altSeparator) {
                    return false;
                }

                var previousChar = path[i - 1];
                if (character == separator) {
                    if (impossibleName || previousChar == separator || char.IsWhiteSpace(previousChar)) {
                        return false;
                    }
                } else if (!IsValidFileNameCharacter(character)) {
                    return false;
                }

                if (character == '.' || char.IsWhiteSpace(character)) {
                    if (previousChar == separator) {
                        impossibleName = true;
                    }
                } else {
                    impossibleName = false;
                }
            }

            var last = path[path.Length - 1];
            return !(impossibleName || last == '.' || char.IsWhiteSpace(last));
        }

        private static bool IsUncStart(string path) {
            var separator = Path.DirectorySeparatorChar;
            return IsWindows && path.Length >= 2 && path[0] == separator && path[1] == separator;
        }

        /// <summary>
        /// Normalizes and returns the provided path.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// If the provided path contains invalid characters.
        /// </exception>
        public static string NormalizePath(string path) {
            if (string.IsNullOrEmpty(path)) {
                return string.Empty;
            }

            if (IsNormalizedPath(path)) {
                return path;
            }

            var root = IsUncStart(path) ? new string(Path.DirectorySeparatorChar, 2) : EnsureEndSeparator(Path.GetPathRoot(path));
            var parts = path.Substring(root.Length).Split(DirectorySeparators);
            var isDir = string.IsNullOrWhiteSpace(parts[parts.Length - 1]);

            for (var i = 0; i < parts.Length; ++i) {
                if (string.IsNullOrEmpty(parts[i])) {
                    if (i > 0) {
                        parts[i] = null;
                    }
                    continue;
                }

                if (parts[i] == ".") {
                    parts[i] = null;
                    continue;
                }

                if (parts[i] == "..") {
                    var found = false;
                    for (var j = i - 1; j >= 0; --j) {
                        if (!string.IsNullOrEmpty(parts[j])) {
                            parts[i] = null;
                            parts[j] = null;
                            found = true;
                            break;
                        }
                    }
                    if (!found && !string.IsNullOrEmpty(root)) {
                        parts[i] = null;
                    }
                    continue;
                }

                parts[i] = parts[i].TrimEnd(' ', '.');
            }

            var newPath = root + string.Join(
                Path.DirectorySeparatorChar.ToString(),
                parts.Where(s => s != null)
            );
            return isDir ? EnsureEndSeparator(newPath) : newPath;
        }

        public static string NormalizePathAndTrim(string path) => TrimEndSeparator(NormalizePath(path));
    }
}
