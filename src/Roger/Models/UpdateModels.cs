using System;

namespace Roger.Models;

public enum UpdatePackageKind { Installer, Portable }

public sealed record UpdateAsset(string Name, long SizeBytes, string DownloadUrl, string? Sha256Digest);

public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes, string Stage)
{
    public double? Percent => TotalBytes is > 0 ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100) : null;
}

/// <summary>A completely downloaded package checked against the release's SHA-256 manifest.</summary>
public sealed record DownloadedUpdate(string FilePath, string Sha256, long SizeBytes, string Repository,
    string Tag, Version Version, UpdatePackageKind Kind, string AssetName);
