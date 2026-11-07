using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;

namespace Roger.Services;

/// <summary>Read-only logical file size analysis. Junctions, symlinks and other reparse points are skipped.</summary>
public sealed class DiskAnalyzer
{
    private const int LargestFileLimit = 100;
    private const int ErrorLimit = 30;

    public Task<DiskScanResult> ScanAsync(string path, IProgress<DiskScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(path, progress, cancellationToken), cancellationToken);

    private static DiskScanResult Scan(string path, IProgress<DiskScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择要分析的文件夹。", nameof(path));
        var root = new DirectoryInfo(Path.GetFullPath(path));
        if (!root.Exists) throw new DirectoryNotFoundException("文件夹不存在或无法访问：" + root.FullName);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("为避免重复计数，不扫描符号链接或目录联接。请选择实际文件夹。");

        var watch = Stopwatch.StartNew();
        var lastProgress = TimeSpan.FromMilliseconds(-200);
        long totalBytes = 0, fileCount = 0, directoryCount = 0, skipped = 0;
        var errors = new List<string>();
        var groups = new List<MutableUsage>();
        var rootFiles = new MutableUsage("[根目录文件]", root.FullName, false);
        var largest = new PriorityQueue<DiskFileEntry, long>();
        // A stack of lazy enumerators bounds memory to directory depth instead of total file count.
        var stack = new Stack<ScanFrame>();

        void Report(string currentPath, bool force = false)
        {
            if (!force && watch.Elapsed - lastProgress < TimeSpan.FromMilliseconds(200)) return;
            lastProgress = watch.Elapsed;
            progress?.Report(new DiskScanProgress(currentPath, totalBytes, fileCount, directoryCount, skipped, watch.Elapsed));
        }

        void Skip(string currentPath, Exception? error = null)
        {
            skipped++;
            if (error != null && errors.Count < ErrorLimit)
                errors.Add(currentPath + " · " + error.Message);
        }

        void Enter(DirectoryInfo directory, MutableUsage? group)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // IgnoreInaccessible is false so omitted directories are reported, not silently hidden.
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0
                };
                var enumerator = directory.EnumerateFileSystemInfos("*", options).GetEnumerator();
                stack.Push(new ScanFrame(directory.FullName, group, enumerator));
                directoryCount++;
            }
            catch (Exception ex) when (IsFileSystemError(ex)) { Skip(directory.FullName, ex); }
        }

        Enter(root, null);
        try
        {
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = stack.Peek();
                FileSystemInfo item;
                try
                {
                    if (!frame.Entries.MoveNext())
                    {
                        stack.Pop().Entries.Dispose();
                        continue;
                    }
                    item = frame.Entries.Current;
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    Skip(frame.Path, ex);
                    stack.Pop().Entries.Dispose();
                    continue;
                }

                try
                {
                    var attributes = item.Attributes;
                    // Never traverse mount points/cloud placeholders/links or read their targets.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Skip(item.FullName);
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        MutableUsage? group = frame.Group;
                        if (stack.Count == 1)
                        {
                            group = new MutableUsage(item.Name, item.FullName, true);
                            groups.Add(group);
                        }
                        Enter((DirectoryInfo)item, group);
                    }
                    else if (item is FileInfo file)
                    {
                        var length = file.Length;
                        totalBytes += length;
                        fileCount++;
                        var group = frame.Group ?? rootFiles;
                        group.Bytes += length;
                        group.FileCount++;
                        if (largest.Count < LargestFileLimit || length > largest.Peek().Bytes)
                        {
                            largest.Enqueue(new DiskFileEntry(file.Name, file.FullName, length), length);
                            if (largest.Count > LargestFileLimit) largest.Dequeue();
                        }
                    }
                }
                catch (Exception ex) when (IsFileSystemError(ex)) { Skip(item.FullName, ex); }
                Report(item.FullName);
            }
        }
        finally
        {
            while (stack.Count > 0) stack.Pop().Entries.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        watch.Stop();
        if (rootFiles.FileCount > 0) groups.Add(rootFiles);
        Report(root.FullName, true);
        return new DiskScanResult(root.FullName, totalBytes, fileCount, directoryCount, skipped,
            groups.OrderByDescending(x => x.Bytes)
                .Select(x => new DiskUsageEntry(x.Name, x.FullPath, x.Bytes, x.FileCount, x.IsDirectory)).ToArray(),
            largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToArray(),
            errors.ToArray(), watch.Elapsed);
    }

    private static bool IsFileSystemError(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;

    private sealed record ScanFrame(string Path, MutableUsage? Group, IEnumerator<FileSystemInfo> Entries);
    private sealed class MutableUsage(string name, string fullPath, bool isDirectory)
    {
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public bool IsDirectory { get; } = isDirectory;
        public long Bytes { get; set; }
        public long FileCount { get; set; }
    }
}
