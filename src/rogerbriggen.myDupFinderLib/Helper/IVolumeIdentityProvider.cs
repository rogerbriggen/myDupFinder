// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderLib;

/// <summary>Whether the identity of the volume a base path lies on could be determined.</summary>
internal enum VolumeIdentityStatus
{
    /// <summary>The identity was determined.</summary>
    Available,

    /// <summary>The base path does not exist (for example the volume is not mounted and the mount point is gone).</summary>
    PathUnavailable,

    /// <summary>The base path exists, but the identity of its volume cannot be determined on this system.</summary>
    NotSupported,
}

/// <summary>The result of <see cref="IVolumeIdentityProvider.GetIdentity"/>.</summary>
/// <param name="Status">What was found.</param>
/// <param name="Identity">The identity string, only set when <paramref name="Status"/> is <see cref="VolumeIdentityStatus.Available"/>.</param>
/// <param name="Detail">A human readable reason, or the mount root that was used.</param>
internal readonly record struct VolumeIdentityResult(VolumeIdentityStatus Status, string? Identity, string? Detail);

/// <summary>Determines an identity string for the volume (disk, partition, mount) a base path lies on.</summary>
internal interface IVolumeIdentityProvider
{
    /// <summary>Returns the identity of the volume of the base path. Never throws.</summary>
    VolumeIdentityResult GetIdentity(string basePath);
}
