// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Receives a raw DLL (or zip) byte stream from an HTTP push request, pre-validates
/// the ABI version without loading the assembly, writes it to the plugin directory
/// atomically, and then triggers <see cref="IPluginReloader.ReloadAsync"/> so the
/// response carries the synchronous reload outcome.
/// </summary>
public interface IAssemblyDllPusher
{
    /// <summary>
    /// Stage, validate, atomically move, and reload a plugin DLL.
    /// </summary>
    /// <param name="pluginName">Plugin folder name (same as the DLL base name).</param>
    /// <param name="dllStream">Raw DLL bytes (<c>application/octet-stream</c>) or a zip archive (<c>application/zip</c>).</param>
    /// <param name="contentType">Either <c>application/octet-stream</c> or <c>application/zip</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AssemblyDllPushResult> PushAsync(
        string pluginName,
        Stream dllStream,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load (or hot-reload) a plugin that is already present in the runtime's plugins
    /// directory. Used by <c>vais plugin import-existing</c> for plugins deployed
    /// via external mechanisms (sidecars, CI/CD). Returns
    /// <see cref="AssemblyDllPushStatus.NotFound"/> when the directory or DLL is absent.
    /// </summary>
    Task<AssemblyDllPushResult> ImportExistingAsync(
        string pluginName,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a <see cref="IAssemblyDllPusher.PushAsync"/> call.</summary>
public sealed record AssemblyDllPushResult(
    string PluginName,
    AssemblyDllPushStatus Status,
    IReadOnlyList<string>? Handlers,
    string? TargetApiVersion,
    string? ErrorMessage);

/// <summary>Outcome categories for an <see cref="IAssemblyDllPusher.PushAsync"/> call.</summary>
public enum AssemblyDllPushStatus
{
    /// <summary>DLL validated, loaded, and registry swapped.</summary>
    Success = 0,
    /// <summary><c>[VaisPlugin].TargetApiVersion</c> does not match the runtime ABI.</summary>
    AbiMismatch = 1,
    /// <summary>PE/IL was invalid or a transitive dependency is missing.</summary>
    LoadFailed = 2,
    /// <summary>Hot-reload is disabled on this runtime.</summary>
    ReloadDisabled = 3,
    /// <summary>Plugin name is unknown (push without a prior apply or startup-load).</summary>
    NotFound = 4,
    /// <summary>First-time load — no prior version existed.</summary>
    Bootstrapped = 5,
    /// <summary>DLL failed pre-validation (missing <c>[VaisPlugin]</c>, handler type absent, etc.).</summary>
    ValidationFailed = 6,
}

internal sealed class AssemblyDllPusher : IAssemblyDllPusher
{
    private readonly IPluginHandlerRegistry _registry;
    private readonly IPluginReloader _reloader;
    private readonly string _pluginsDirectory;
    private readonly string _runtimeAbiVersion;
    private readonly ILogger<AssemblyDllPusher> _logger;

    internal AssemblyDllPusher(
        IPluginHandlerRegistry registry,
        IPluginReloader reloader,
        string pluginsDirectory,
        string runtimeAbiVersion,
        ILogger<AssemblyDllPusher>? logger = null)
    {
        _registry = registry;
        _reloader = reloader;
        _pluginsDirectory = pluginsDirectory;
        _runtimeAbiVersion = runtimeAbiVersion;
        _logger = logger ?? NullLogger<AssemblyDllPusher>.Instance;
    }

    public async Task<AssemblyDllPushResult> PushAsync(
        string pluginName,
        Stream dllStream,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentNullException.ThrowIfNull(dllStream);

        // 1. Stage to a temp directory under <pluginsDirectory>/.staging/<pluginName>-<guid>/
        var stagingRoot = Path.Combine(_pluginsDirectory, ".staging");
        var stagingDir = Path.Combine(stagingRoot, $"{pluginName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            string primaryDllPath;
            if (IsZip(contentType))
            {
                var extractResult = await ExtractZipAsync(dllStream, stagingDir, pluginName, cancellationToken)
                    .ConfigureAwait(false);
                if (extractResult is null)
                    return Fail(pluginName, AssemblyDllPushStatus.ValidationFailed,
                        $"Zip archive must contain '{pluginName}.dll' or a DLL matching '*{pluginName}.dll'.");
                primaryDllPath = extractResult;
            }
            else
            {
                primaryDllPath = Path.Combine(stagingDir, pluginName + ".dll");
                await using var fs = new FileStream(primaryDllPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await dllStream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            // 2. Pre-validate ABI via PEReader (no load into ALC, so no file lock or code execution).
            var abiResult = TryReadVaisPluginAttribute(primaryDllPath);
            if (abiResult is null)
                return Fail(pluginName, AssemblyDllPushStatus.ValidationFailed,
                    "The DLL has no [assembly: VaisPlugin] attribute. Add it and rebuild.");

            var (pluginAbi, handlers) = abiResult.Value;
            if (!AbiMatches(pluginAbi, _runtimeAbiVersion))
                return Fail(pluginName, AssemblyDllPushStatus.AbiMismatch,
                    $"DLL targets ABI '{pluginAbi}'; runtime expects '{_runtimeAbiVersion}'. " +
                    $"Update [assembly: VaisPlugin(targetApiVersion: \"{_runtimeAbiVersion}\")] and rebuild.");

            // 3. Determine target folder and whether this is a first-load.
            var targetDir = Path.GetFullPath(Path.Combine(_pluginsDirectory, pluginName));
            var isBootstrap = !_registry.Plugins.Any(p =>
                string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));

            // 4. Atomic move: staging → target.
            // On Windows, File.Move cannot overwrite a DLL that is locked by a loaded ALC.
            // Directory.Move renames the directory entry itself without touching file handles,
            // so it succeeds even when files inside are locked. Strategy:
            //   a) rename the existing target away (targetDir → retiredDir)
            //   b) rename staging into its place (stagingDir → targetDir)
            //   c) best-effort delete the retired directory (may stay locked until GC)
            var retiredDir = targetDir + "-retired-" + DateTimeOffset.UtcNow.Ticks.ToString("x");
            bool targetExisted = Directory.Exists(targetDir);
            if (targetExisted)
                Directory.Move(targetDir, retiredDir);
            try
            {
                Directory.Move(stagingDir, targetDir);
            }
            catch
            {
                // Restore the old directory if we can't move staging in.
                if (targetExisted)
                    try { Directory.Move(retiredDir, targetDir); } catch { }
                throw;
            }
            // Best-effort cleanup of the retired directory (Windows: may stay locked until GC).
            try { Directory.Delete(retiredDir, recursive: true); } catch { }

            // Also clean up any lingering retired directories from prior pushes.
            try
            {
                var pluginsParent = Path.GetDirectoryName(targetDir)!;
                foreach (var old in Directory.GetDirectories(pluginsParent, pluginName + "-retired-*"))
                    try { Directory.Delete(old, recursive: true); } catch { }
            }
            catch { }

            // 5. Trigger reload directly (bypassing the watcher for synchronous outcome).
            var dllTarget = Path.Combine(targetDir, pluginName + ".dll");
            if (!File.Exists(dllTarget))
            {
                // Fallback: find the primary DLL in the target directory.
                dllTarget = ResolvePrimaryInDirectory(targetDir, pluginName) ?? dllTarget;
            }

            var reloadResult = await _reloader.ReloadAsync(dllTarget, cancellationToken).ConfigureAwait(false);

            return reloadResult.Status switch
            {
                PluginReloadStatus.Success => new AssemblyDllPushResult(
                    pluginName,
                    isBootstrap ? AssemblyDllPushStatus.Bootstrapped : AssemblyDllPushStatus.Success,
                    reloadResult.NewDescriptor?.Handlers?.ToList(),
                    reloadResult.NewDescriptor?.TargetApiVersion,
                    null),
                PluginReloadStatus.AbiMismatch => Fail(pluginName, AssemblyDllPushStatus.AbiMismatch,
                    "ABI mismatch detected during load."),
                _ => Fail(pluginName, AssemblyDllPushStatus.LoadFailed,
                    reloadResult.FailureException?.Message ?? "Plugin load failed."),
            };
        }
        finally
        {
            // Best-effort cleanup of the staging directory. File locks (Windows) may prevent
            // cleanup immediately; the .staging folder can be cleaned up on the next push.
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    public async Task<AssemblyDllPushResult> ImportExistingAsync(
        string pluginName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);

        var pluginDir = Path.Combine(_pluginsDirectory, pluginName);
        if (!Directory.Exists(pluginDir))
            return Fail(pluginName, AssemblyDllPushStatus.NotFound,
                $"Plugin directory not found: {pluginDir}");

        var dllPath = Path.Combine(pluginDir, pluginName + ".dll");
        if (!File.Exists(dllPath))
            dllPath = ResolvePrimaryInDirectory(pluginDir, pluginName) ?? dllPath;

        if (!File.Exists(dllPath))
            return Fail(pluginName, AssemblyDllPushStatus.NotFound,
                $"Plugin DLL not found in '{pluginDir}'.");

        var isBootstrap = !_registry.Plugins.Any(p =>
            string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));

        var reloadResult = await _reloader.ReloadAsync(dllPath, cancellationToken).ConfigureAwait(false);

        return reloadResult.Status switch
        {
            PluginReloadStatus.Success => new AssemblyDllPushResult(
                pluginName,
                isBootstrap ? AssemblyDllPushStatus.Bootstrapped : AssemblyDllPushStatus.Success,
                reloadResult.NewDescriptor?.Handlers?.ToList(),
                reloadResult.NewDescriptor?.TargetApiVersion,
                null),
            PluginReloadStatus.AbiMismatch => Fail(pluginName, AssemblyDllPushStatus.AbiMismatch,
                "ABI mismatch detected during import."),
            _ => Fail(pluginName, AssemblyDllPushStatus.LoadFailed,
                reloadResult.FailureException?.Message ?? "Plugin load failed."),
        };
    }

    private static AssemblyDllPushResult Fail(string pluginName, AssemblyDllPushStatus status, string message) =>
        new(pluginName, status, null, null, message);

    private static bool IsZip(string contentType) =>
        contentType.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase);

    // Returns the path to the primary DLL if found, null if the zip is invalid.
    private static async Task<string?> ExtractZipAsync(
        Stream zipStream, string stagingDir, string pluginName, CancellationToken ct)
    {
        using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        string? primaryDll = null;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;

            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(stagingDir, rel));
            if (!dest.StartsWith(stagingDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue; // Path traversal guard — skip

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using var entryStream = entry.Open();
            await using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await entryStream.CopyToAsync(destStream, ct).ConfigureAwait(false);

            // Identify the primary DLL: exact name match or suffix match.
            var fileName = Path.GetFileName(dest);
            if (string.Equals(fileName, pluginName + ".dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("." + pluginName + ".dll", StringComparison.OrdinalIgnoreCase))
            {
                primaryDll = dest;
            }
        }

        return primaryDll;
    }

    private static string? ResolvePrimaryInDirectory(string dir, string pluginName)
    {
        var exact = Path.Combine(dir, pluginName + ".dll");
        if (File.Exists(exact)) return exact;

        return Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(p => Path.GetFileName(p).EndsWith("." + pluginName + ".dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the <c>[VaisPlugin]</c> assembly-level custom attribute from a DLL using
    /// <see cref="PEReader"/> + <see cref="MetadataReader"/> without loading the
    /// assembly into any <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
    /// Returns (targetApiVersion, handlers) on success, or null when the attribute is absent.
    /// </summary>
    private static (string abi, IReadOnlyList<string> handlers)? TryReadVaisPluginAttribute(string dllPath)
    {
        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return null;

            var metadata = peReader.GetMetadataReader();
            foreach (var attrHandle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = metadata.GetCustomAttribute(attrHandle);
                // Check constructor's declaring type name.
                string typeName = GetAttrTypeName(metadata, attr.Constructor);
                if (!typeName.EndsWith("VaisPluginAttribute", StringComparison.Ordinal))
                    continue;

                // Decode the blob: prolog (01 00) + SerString(abi) + Int32(count) + SerString[]
                var blob = metadata.GetBlobReader(attr.Value);
                if (blob.Length < 4) continue;

                // Skip prolog
                blob.ReadUInt16(); // 0x0001

                var abi = ReadSerString(ref blob);
                if (abi is null) continue;

                var handlers = new List<string>();
                // The params string[] is encoded as array length (Int32LE) + elements
                if (blob.RemainingBytes >= 4)
                {
                    var count = blob.ReadInt32();
                    if (count > 0 && count < 1024)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            if (blob.RemainingBytes == 0) break;
                            var h = ReadSerString(ref blob);
                            if (h is not null) handlers.Add(h);
                        }
                    }
                }

                return (abi, handlers);
            }
        }
        catch (Exception)
        {
            // Malformed PE or unsupported format — caller treats as ValidationFailed.
        }
        return null;
    }

    private static string GetAttrTypeName(MetadataReader metadata, EntityHandle ctorHandle)
    {
        if (ctorHandle.Kind == HandleKind.MemberReference)
        {
            var memberRef = metadata.GetMemberReference((MemberReferenceHandle)ctorHandle);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = metadata.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                return metadata.GetString(typeRef.Name);
            }
        }
        else if (ctorHandle.Kind == HandleKind.MethodDefinition)
        {
            var methodDef = metadata.GetMethodDefinition((MethodDefinitionHandle)ctorHandle);
            var typeDef = metadata.GetTypeDefinition(methodDef.GetDeclaringType());
            return metadata.GetString(typeDef.Name);
        }
        return string.Empty;
    }

    // ECMA-335 §II.23.3 SerString: 0xFF = null, otherwise compressed-length + UTF-8.
    private static string? ReadSerString(ref BlobReader blob)
    {
        if (blob.RemainingBytes == 0) return null;
        var first = blob.ReadByte();
        if (first == 0xFF) return null; // null string

        int length;
        if ((first & 0x80) == 0)
        {
            length = first;
        }
        else if ((first & 0xC0) == 0x80)
        {
            if (blob.RemainingBytes == 0) return null;
            length = ((first & 0x3F) << 8) | blob.ReadByte();
        }
        else if ((first & 0xE0) == 0xC0)
        {
            if (blob.RemainingBytes < 3) return null;
            length = ((first & 0x1F) << 24)
                   | (blob.ReadByte() << 16)
                   | (blob.ReadByte() << 8)
                   | blob.ReadByte();
        }
        else
        {
            return null;
        }

        if (length < 0 || blob.RemainingBytes < length) return null;
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = blob.ReadByte();
        return Encoding.UTF8.GetString(bytes);
    }

    // Same major-minor rule as AssemblyPluginLoader.AbiMatches.
    private static bool AbiMatches(string pluginVersion, string runtimeVersion)
    {
        if (string.Equals(pluginVersion, runtimeVersion, StringComparison.Ordinal)) return true;
        if (TryParseMajorMinor(pluginVersion, out var pv) && TryParseMajorMinor(runtimeVersion, out var rv))
            return pv == rv;
        return false;
    }

    private static bool TryParseMajorMinor(string version, out (int major, int minor) mm)
    {
        mm = default;
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;
        mm = (major, minor);
        return true;
    }
}
