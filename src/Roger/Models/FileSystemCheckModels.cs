using System;

namespace Roger.Models;

public enum FileSystemCheckState { Healthy, AttentionRequired, Incomplete }

/// <summary>Append OutputChunk to the in-app report. CHKDSK does not provide a reliable overall percentage.</summary>
public sealed record FileSystemCheckProgress(string OutputChunk, TimeSpan Elapsed);

public sealed record FileSystemCheckResult(
    string Volume, int ExitCode, FileSystemCheckState State, string Summary,
    string Report, TimeSpan Elapsed, DateTimeOffset CompletedAt);
