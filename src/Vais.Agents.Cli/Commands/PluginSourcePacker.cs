// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Formats.Tar;
using System.IO.Compression;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Packs a plugin source directory into an in-memory gzip-compressed tar archive.
/// Shared by <see cref="PluginPushCommand"/> and <see cref="PluginWatchCommand"/>.
/// </summary>
internal static class PluginSourcePacker
{
    private static readonly HashSet<string> ExcludedDirs = [".venv", "__pycache__", ".git"];
    private static readonly HashSet<string> ExcludedExtensions = [".pyc", ".pyo"];

    /// <summary>
    /// Returns a seekable <see cref="MemoryStream"/> containing a gzip-compressed tar archive
    /// of <paramref name="sourceDir"/>. The stream is positioned at offset 0 on return.
    /// </summary>
    public static MemoryStream Pack(string sourceDir)
    {
        var ms = new MemoryStream();
        // Use the parent of sourceDir as rootDir so entries are "src/foo.py" not "foo.py".
        // The server unpacks to <pluginDirectory>/<entry>, so "src/foo.py" lands correctly.
        var rootDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sourceDir)) ?? sourceDir;
        using (var gzip = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            PackDirectory(writer, rootDir, sourceDir);
        }
        ms.Position = 0;
        return ms;
    }

    private static void PackDirectory(TarWriter writer, string rootDir, string currentDir)
    {
        foreach (var file in Directory.EnumerateFiles(currentDir))
        {
            if (ExcludedExtensions.Contains(Path.GetExtension(file)))
                continue;
            var relativePath = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
            writer.WriteEntry(file, relativePath);
        }
        foreach (var dir in Directory.EnumerateDirectories(currentDir))
        {
            if (ExcludedDirs.Contains(Path.GetFileName(dir)))
                continue;
            PackDirectory(writer, rootDir, dir);
        }
    }
}
