// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Decides whether a path lies below one of the directories that could not be read. Used by refresh (do not remove the row) and
/// check (do not report the row as missing). The decision errs on the side of protecting:
/// <list type="bullet">
/// <item>Only at a directory boundary: <c>C:\data\a</c> protects <c>C:\data\a\f.txt</c> but not <c>C:\data\ab\f.txt</c>. The directory itself counts as well.</item>
/// <item>Case-insensitive on every platform (the file system of a network share is often not what the OS is).</item>
/// <item>Both separator characters are the same, repeated separators count as one, a trailing separator is ignored.</item>
/// <item>A directory is also known by its full path (<see cref="Path.GetFullPath(string)"/>), in case the base path was given relative.</item>
/// <item>With a relative lookup (check with IgnoreBasePath, rows that were scanned under another base path): the part of the path below its own
/// base path is compared with the part of the unreadable directories below the current base path. A path that is not below its own base path is protected as soon as there is any unreadable directory.</item>
/// </list>
/// </summary>
internal sealed class UnreadableDirectoryMatcher
{
    private const char Separator = '\\';

    private readonly HashSet<string> _absolute = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _relative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <param name="unreadableDirectories">Full paths of the directories that could not be read.</param>
    /// <param name="basePath">The base path of the run. The unreadable directories are below it.</param>
    public UnreadableDirectoryMatcher(IEnumerable<string> unreadableDirectories, string basePath)
    {
        ArgumentNullException.ThrowIfNull(unreadableDirectories);
        ArgumentNullException.ThrowIfNull(basePath);
        string normalizedBase = Normalize(basePath);
        foreach (string directory in unreadableDirectories)
        {
            string normalized = Normalize(directory);
            _absolute.Add(normalized);
            try
            {
                _absolute.Add(Normalize(Path.GetFullPath(directory)));
            }
#pragma warning disable CA1031 // Do not catch general exception types: the plain form is in the set already
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            if (TryGetRelative(normalized, normalizedBase, out string relative) && relative.Length > 0)
            {
                _relative.Add(relative);
            }
        }
    }

    /// <summary>True when no directory was unreadable, then nothing is protected.</summary>
    public bool IsEmpty => _absolute.Count == 0;

    /// <summary>Number of different unreadable directories.</summary>
    public int Count => _absolute.Count;

    /// <summary>
    /// True when the path is the unreadable directory itself or below it.
    /// </summary>
    public bool IsProtected(string path)
    {
        if (IsEmpty)
        {
            return false;
        }
        return IsInSet(Normalize(path), _absolute);
    }

    /// <summary>
    /// Like <see cref="IsProtected(string)"/>, and also true when the path relative to <paramref name="pathBase"/> (the base path the row was scanned under)
    /// is the relative path of an unreadable directory or below it. Also true when the path is not below <paramref name="pathBase"/> and any directory was unreadable,
    /// because then it is not known where the file would be.
    /// </summary>
    public bool IsProtectedByAnyBasePath(string path, string pathBase)
    {
        if (IsEmpty)
        {
            return false;
        }
        string normalized = Normalize(path);
        if (IsInSet(normalized, _absolute))
        {
            return true;
        }
        if (!TryGetRelative(normalized, Normalize(pathBase), out string relative))
        {
            return true;
        }
        return IsInSet(relative, _relative);
    }

    private static bool TryGetRelative(string normalizedPath, string normalizedBase, out string relative)
    {
        relative = string.Empty;
        if (!normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (normalizedPath.Length == normalizedBase.Length)
        {
            return true;
        }
        if (normalizedPath[normalizedBase.Length] != Separator)
        {
            return false;
        }
        relative = normalizedPath.Substring(normalizedBase.Length + 1);
        return true;
    }

    /// <summary>The path itself or one of its parent directories is in the set.</summary>
    private static bool IsInSet(string normalizedPath, HashSet<string> set)
    {
        if (set.Count == 0)
        {
            return false;
        }
        var lookup = set.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.Contains(normalizedPath.AsSpan()))
        {
            return true;
        }
        int index = normalizedPath.LastIndexOf(Separator);
        while (index >= 0)
        {
            // index 0 gives the empty string: a root directory that is unreadable ("/" or "\" after normalizing)
            if (lookup.Contains(normalizedPath.AsSpan(0, index)))
            {
                return true;
            }
            index = index == 0 ? -1 : normalizedPath.LastIndexOf(Separator, index - 1);
        }
        return false;
    }

    /// <summary>
    /// Both separators become one backslash, repeated separators are collapsed (a leading pair for a UNC path stays), the trailing separator is removed.
    /// </summary>
    internal static string Normalize(string path)
    {
        bool needsWork = path.Contains('/', StringComparison.Ordinal) || path.EndsWith(Separator) || (path.Length > 1 && path.IndexOf("\\\\", 1, StringComparison.Ordinal) >= 0);
        if (!needsWork)
        {
            return path;
        }
        var builder = new StringBuilder(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i] == '/' ? Separator : path[i];
            if (c == Separator && builder.Length > 0 && builder[^1] == Separator && i > 1)
            {
                continue;
            }
            builder.Append(c);
        }
        while (builder.Length > 0 && builder[^1] == Separator)
        {
            builder.Length--;
        }
        return builder.ToString();
    }
}

/// <summary>
/// Writes the warnings about unreadable directories.
/// </summary>
internal static class UnreadableDirectoryLog
{
    /// <summary>
    /// Logs a warning for every directory in the list that was not logged yet and advances <paramref name="alreadyLogged"/>.
    /// Cheap enough to call after every MoveNext.
    /// </summary>
    public static void LogNew(ILogger logger, IReadOnlyList<UnreadableDirectory> directories, ref int alreadyLogged)
    {
        while (alreadyLogged < directories.Count)
        {
            UnreadableDirectory directory = directories[alreadyLogged];
            alreadyLogged++;
            logger.LogWarning("Directory {directory} could not be read completely ({errorType}: {error}). The files below it are not part of this run.", directory.Path, directory.Error.GetType().Name, directory.Error.Message);
        }
    }

    /// <summary>The directories as one text for a summary line.</summary>
    public static string Join(IReadOnlyList<UnreadableDirectory> directories)
    {
        var paths = new string[directories.Count];
        for (int i = 0; i < paths.Length; i++)
        {
            paths[i] = directories[i].Path;
        }
        return string.Join("; ", paths);
    }
}
