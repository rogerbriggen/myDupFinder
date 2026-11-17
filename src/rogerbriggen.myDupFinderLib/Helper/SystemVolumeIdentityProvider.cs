// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Determines the identity of the volume of a base path from the operating system. Links (symbolic links, junctions) in the base path are followed first, so the identity is the one
/// of the volume the files really lie on (Unix: realpath; Windows: the final path name of the opened directory).
/// Windows: the volume GUID of the mount root (<c>win-volume:\\?\Volume{...}\</c>, mounted folders are handled); for volumes that have none (network shares, mapped drives, SUBST)
/// the serial number and file system name (<c>win-serial:XXXXXXXX:NTFS</c>).
/// Linux: the UUID of the file system that is mounted at the mount root (<c>linux-uuid:...</c>) when /dev/disk/by-uuid has it, otherwise <c>mount:root|fstype|source</c> with the mount source
/// from /proc/self/mountinfo (for example <c>/dev/sdb1</c>, <c>server:/export</c>, <c>pool/dataset</c>), or <c>mount:root|format</c> if the source is not known.
/// Other systems: <c>mount:root|format</c>.
/// Limits: the <c>mount:root|format</c> form detects an unmounted volume (the base path then lies on the parent file system, which has another mount root), but not a different disk with the same
/// file system type at the same mount point. A <c>win-serial</c> identity changes when the volume is reformatted. A volume GUID changes when the volume is recreated or cloned to a new partition.
/// </summary>
internal sealed class SystemVolumeIdentityProvider : IVolumeIdentityProvider
{
    /// <summary>The one instance.</summary>
    public static SystemVolumeIdentityProvider Instance { get; } = new SystemVolumeIdentityProvider();

    /// <inheritdoc />
    public VolumeIdentityResult GetIdentity(string basePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            {
                return new VolumeIdentityResult(VolumeIdentityStatus.PathUnavailable, null, "the directory does not exist");
            }
            string full = Path.GetFullPath(basePath);
            if (OperatingSystem.IsWindows())
            {
                return GetWindowsIdentity(ResolveWindowsFinalPath(full));
            }
            var (canonical, failure) = ResolveUnixRealPath(full);
            if (failure is not null)
            {
                return failure.Value;
            }
            if (OperatingSystem.IsLinux())
            {
                return GetLinuxIdentity(canonical!);
            }
            return GetMountIdentity(canonical!);
        }
#pragma warning disable CA1031 // The identity is a safety check: whatever goes wrong means "cannot be determined", never a crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new VolumeIdentityResult(VolumeIdentityStatus.NotSupported, null, ex.Message);
        }
    }

    // ---- Unix: follow all links ----

    private const int ENOENT = 2;

    [DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr ptr);

    private static (string? Path, VolumeIdentityResult? Failure) ResolveUnixRealPath(string fullPath)
    {
        IntPtr result = RealPath(fullPath, IntPtr.Zero);
        if (result == IntPtr.Zero)
        {
            int errno = Marshal.GetLastWin32Error();
            var status = errno == ENOENT ? VolumeIdentityStatus.PathUnavailable : VolumeIdentityStatus.NotSupported;
            return (null, new VolumeIdentityResult(status, null, $"realpath failed (errno {errno})"));
        }
        try
        {
            string? resolved = Marshal.PtrToStringUTF8(result);
            if (string.IsNullOrEmpty(resolved))
            {
                return (null, new VolumeIdentityResult(VolumeIdentityStatus.NotSupported, null, "realpath returned nothing"));
            }
            return (resolved, null);
        }
        finally
        {
            Free(result);
        }
    }

    // ---- Windows ----

    private const int MaxPath = 32768;
    private const uint FileShareAll = 0x7; // READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string lpszFileName, char[] lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, char[] lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string lpRootPathName, char[]? lpVolumeNameBuffer, uint nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, char[] lpFileSystemNameBuffer, uint nFileSystemNameSize);

    private static string FromBuffer(char[] buffer)
    {
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>
    /// Turns the final path name of Windows (<c>\\?\C:\dir</c>, <c>\\?\UNC\server\share\dir</c>) back into the usual form (<c>C:\dir</c>, <c>\\server\share\dir</c>).
    /// Anything else (for example <c>\\?\Volume{...}\</c>) is returned as it is.
    /// </summary>
    internal static string StripWin32Prefix(string path)
    {
        const string Unc = @"\\?\UNC\";
        const string Prefix = @"\\?\";
        if (path.StartsWith(Unc, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path.Substring(Unc.Length);
        }
        if (path.StartsWith(Prefix, StringComparison.Ordinal) && path.Length >= Prefix.Length + 2 && char.IsAsciiLetter(path[Prefix.Length]) && path[Prefix.Length + 1] == ':')
        {
            return path.Substring(Prefix.Length);
        }
        return path;
    }

    /// <summary>The path with all junctions and symbolic links followed. If that fails, the original path: the final path only improves the identity, it never fails it.</summary>
    private static string ResolveWindowsFinalPath(string fullPath)
    {
        try
        {
            using SafeFileHandle handle = CreateFile(fullPath, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return fullPath;
            }
            var buffer = new char[MaxPath];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
            if (length == 0 || length >= buffer.Length)
            {
                return fullPath;
            }
            return StripWin32Prefix(new string(buffer, 0, (int)length));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return fullPath;
        }
    }

    private static VolumeIdentityResult GetWindowsIdentity(string fullPath)
    {
        var rootBuffer = new char[MaxPath];
        if (!GetVolumePathName(fullPath, rootBuffer, (uint)rootBuffer.Length))
        {
            return new VolumeIdentityResult(VolumeIdentityStatus.NotSupported, null, $"GetVolumePathName failed (Win32 error {Marshal.GetLastWin32Error()})");
        }
        string root = FromBuffer(rootBuffer);

        var nameBuffer = new char[64];
        if (GetVolumeNameForVolumeMountPoint(root, nameBuffer, (uint)nameBuffer.Length))
        {
            return new VolumeIdentityResult(VolumeIdentityStatus.Available, "win-volume:" + FromBuffer(nameBuffer), root);
        }
        int guidError = Marshal.GetLastWin32Error();

        // Network shares, mapped drives and SUBST drives have no volume GUID
        var fsBuffer = new char[64];
        if (GetVolumeInformation(root, null, 0, out uint serial, out _, out _, fsBuffer, (uint)fsBuffer.Length))
        {
            return new VolumeIdentityResult(VolumeIdentityStatus.Available, $"win-serial:{serial:X8}:{FromBuffer(fsBuffer)}", root);
        }
        int infoError = Marshal.GetLastWin32Error();
        return new VolumeIdentityResult(VolumeIdentityStatus.NotSupported, null, $"no volume GUID for {root} (Win32 error {guidError}) and no volume serial number (Win32 error {infoError})");
    }

    // ---- Linux and others ----

    private static string TrimRoot(string root) => root.Length > 1 ? root.TrimEnd('/') : root;

    /// <summary>The mount root (the drive with the longest matching path prefix) and its file system type.</summary>
    private static (string Root, string Format) FindMountRoot(string fullPath)
    {
        string path = TrimRoot(fullPath);
        string bestRoot = string.Empty;
        string bestFormat = string.Empty;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string root = TrimRoot(drive.RootDirectory.FullName);
            bool matches = root == "/" || string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal);
            if (matches && root.Length >= bestRoot.Length)
            {
                bestRoot = root;
                try
                {
                    bestFormat = drive.DriveFormat;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    bestFormat = "unknown";
                }
            }
        }
        if (bestRoot.Length == 0)
        {
            throw new InvalidOperationException("no mount root found for " + fullPath);
        }
        return (bestRoot, bestFormat);
    }

    private static VolumeIdentityResult GetMountIdentity(string fullPath)
    {
        var (root, format) = FindMountRoot(fullPath);
        return new VolumeIdentityResult(VolumeIdentityStatus.Available, $"mount:{root}|{format}", root);
    }

    private static VolumeIdentityResult GetLinuxIdentity(string fullPath)
    {
        var (root, format) = FindMountRoot(fullPath);
        string identity = $"mount:{root}|{format}";
        try
        {
            var (source, fsType) = ReadMountInfo(root);
            string? uuid = source is null ? null : FindUuid(source);
            if (uuid is not null)
            {
                identity = $"linux-uuid:{uuid}";
            }
            else if (source is not null)
            {
                identity = $"mount:{root}|{fsType ?? format}|{source}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not readable here: the plain mount form
        }
        return new VolumeIdentityResult(VolumeIdentityStatus.Available, identity, root);
    }

    /// <summary>The mount source and the file system type of the mount at the mount root, from /proc/self/mountinfo (null when not known).</summary>
    private static (string? Source, string? FsType) ReadMountInfo(string mountRoot)
    {
        const string MountInfo = "/proc/self/mountinfo";
        if (!File.Exists(MountInfo))
        {
            return (null, null);
        }
        string? source = null;
        string? fsType = null;
        foreach (string line in File.ReadLines(MountInfo))
        {
            // id parent major:minor root mountpoint options [optional fields] - fstype source superoptions
            int dash = line.IndexOf(" - ", StringComparison.Ordinal);
            if (dash < 0)
            {
                continue;
            }
            string[] head = line.Substring(0, dash).Split(' ');
            string[] tail = line.Substring(dash + 3).Split(' ');
            if (head.Length < 5 || tail.Length < 2)
            {
                continue;
            }
            if (string.Equals(TrimRoot(UnescapeMountInfo(head[4])), mountRoot, StringComparison.Ordinal))
            {
                // The last entry for a mount point is the one that is visible
                fsType = tail[0];
                source = UnescapeMountInfo(tail[1]);
            }
        }
        if (string.IsNullOrEmpty(source) || source == "none")
        {
            return (null, fsType);
        }
        return (source, fsType);
    }

    private static string? FindUuid(string source)
    {
        const string ByUuid = "/dev/disk/by-uuid";
        if (!source.StartsWith('/') || !Directory.Exists(ByUuid))
        {
            return null;
        }
        string device = Resolve(source);
        foreach (string link in Directory.EnumerateFileSystemEntries(ByUuid))
        {
            if (string.Equals(Resolve(link), device, StringComparison.Ordinal))
            {
                return Path.GetFileName(link);
            }
        }
        return null;
    }

    private static string Resolve(string path)
    {
        FileSystemInfo? target = File.ResolveLinkTarget(path, returnFinalTarget: true);
        return target?.FullName ?? Path.GetFullPath(path);
    }

    /// <summary>Mount info writes space, tab, newline and backslash as three digit octal escapes.</summary>
    private static string UnescapeMountInfo(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && IsOctal(value, i + 1))
            {
                result.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                result.Append(value[i]);
            }
        }
        return result.ToString();
    }

    private static bool IsOctal(string value, int start)
    {
        if (start + 3 > value.Length)
        {
            return false;
        }
        for (int i = start; i < start + 3; i++)
        {
            if (value[i] < '0' || value[i] > '7')
            {
                return false;
            }
        }
        return true;
    }
}
