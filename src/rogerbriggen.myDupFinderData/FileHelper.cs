// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;

namespace RogerBriggen.MyDupFinderData;

public class FileHelper
{
    public static bool EndsWithDirectoryDelimiter(string pathToCheck)
    {
        if (pathToCheck.EndsWith(Path.DirectorySeparatorChar))
        {
            return true;
        }
        if (pathToCheck.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return true;
        }
        return false;
    }

    public static string AddDirectoryDelimiter(string path)
    {
        if (EndsWithDirectoryDelimiter(path))
        {
            //Nothing to do...
            return path;
        }
        return path + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// True when <paramref name="path"/> is the same directory as <paramref name="basePath"/> or lies below it.
    /// Both paths are made absolute with <see cref="Path.GetFullPath(string)"/> first (relative paths and <c>.</c> / <c>..</c> segments are resolved against the current directory),
    /// a trailing separator is ignored and the comparison stops at separator boundaries: <c>C:\data2\x.db</c> is not below <c>C:\data</c>, <c>C:\data</c> is the same as <c>C:\data\</c>.
    /// A root such as <c>C:\</c> or <c>/</c> as base contains everything on that root. The comparison ignores case on Windows and is case sensitive elsewhere.
    /// </summary>
    /// <exception cref="ArgumentException">A path is not valid.</exception>
    /// <exception cref="NotSupportedException">A path is not valid.</exception>
    /// <exception cref="PathTooLongException">A path is too long.</exception>
    public static bool IsSameOrBelow(string path, string basePath)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullPath.Equals(fullBase, comparison))
        {
            return true;
        }
        // A root keeps its separator (C:\ or /), then the prefix test is enough; otherwise the next character must be a separator
        string prefix = EndsWithDirectoryDelimiter(fullBase) ? fullBase : fullBase + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison);
    }
}
