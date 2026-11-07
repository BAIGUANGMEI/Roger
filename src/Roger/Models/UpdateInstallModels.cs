using System;

namespace Roger.Models;

public enum UpdateInstallKind { Installer, Portable }
public enum UpdateInstallStatus { Succeeded, Failed, RestartRequired }

public sealed record UpdateInstallLaunch(Guid RequestId, string StageDirectory);
public sealed record UpdateInstallResult(Guid RequestId, DateTimeOffset FinishedAt, string TargetVersion,
    UpdateInstallStatus Status, string Message, string LogPath, int? ExitCode)
{
    public bool Succeeded => Status == UpdateInstallStatus.Succeeded;
    public bool RestartRequired => Status == UpdateInstallStatus.RestartRequired;
}

public sealed record UpdateInstallRequest(Guid Id, DateTimeOffset CreatedAt, UpdateInstallKind Kind,
    string TargetDirectory, string TargetVersion, string PackageSha256, long PackageSize,
    int ParentProcessId, long ParentStartTimeUtcTicks, string ParentExecutable);
