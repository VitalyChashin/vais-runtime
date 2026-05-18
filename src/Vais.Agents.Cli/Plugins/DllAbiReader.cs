// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Vais.Agents.Cli.Plugins;

/// <summary>
/// Reads the <c>[VaisPlugin]</c> ABI version from a DLL without loading the
/// assembly. Used by the CLI to pre-validate before uploading.
/// </summary>
internal static class DllAbiReader
{
    /// <summary>Current runtime ABI version — must stay in sync with <c>VaisRuntimeAbi.CurrentVersion</c>.</summary>
    internal const string RuntimeAbiVersion = "0.18";

    /// <summary>
    /// Returns the <c>targetApiVersion</c> string from the assembly-level
    /// <c>[VaisPlugin]</c> attribute, or <c>null</c> when the attribute is
    /// absent or the file is not a valid PE.
    /// </summary>
    internal static string? TryReadAbi(string dllPath)
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
                if (!TypeNameEndsWith(metadata, attr.Constructor, "VaisPluginAttribute"))
                    continue;

                var blob = metadata.GetBlobReader(attr.Value);
                if (blob.Length < 4) continue;
                blob.ReadUInt16(); // prolog 0x0001
                return ReadSerString(ref blob);
            }
        }
        catch
        {
            // Malformed PE — caller treats as unknown ABI.
        }
        return null;
    }

    internal static bool AbiMatches(string pluginAbi, string runtimeAbi)
    {
        if (string.Equals(pluginAbi, runtimeAbi, StringComparison.Ordinal)) return true;
        if (TryParseMajorMinor(pluginAbi, out var pv) && TryParseMajorMinor(runtimeAbi, out var rv))
            return pv == rv;
        return false;
    }

    private static bool TypeNameEndsWith(MetadataReader metadata, EntityHandle ctorHandle, string suffix)
    {
        if (ctorHandle.Kind == HandleKind.MemberReference)
        {
            var memberRef = metadata.GetMemberReference((MemberReferenceHandle)ctorHandle);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = metadata.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                return metadata.GetString(typeRef.Name).EndsWith(suffix, StringComparison.Ordinal);
            }
        }
        else if (ctorHandle.Kind == HandleKind.MethodDefinition)
        {
            var methodDef = metadata.GetMethodDefinition((MethodDefinitionHandle)ctorHandle);
            var typeDef = metadata.GetTypeDefinition(methodDef.GetDeclaringType());
            return metadata.GetString(typeDef.Name).EndsWith(suffix, StringComparison.Ordinal);
        }
        return false;
    }

    private static string? ReadSerString(ref BlobReader blob)
    {
        if (blob.RemainingBytes == 0) return null;
        var first = blob.ReadByte();
        if (first == 0xFF) return null;

        int length;
        if ((first & 0x80) == 0)
            length = first;
        else if ((first & 0xC0) == 0x80)
        {
            if (blob.RemainingBytes == 0) return null;
            length = ((first & 0x3F) << 8) | blob.ReadByte();
        }
        else if ((first & 0xE0) == 0xC0)
        {
            if (blob.RemainingBytes < 3) return null;
            length = ((first & 0x1F) << 24) | (blob.ReadByte() << 16) | (blob.ReadByte() << 8) | blob.ReadByte();
        }
        else return null;

        if (length < 0 || blob.RemainingBytes < length) return null;
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = blob.ReadByte();
        return Encoding.UTF8.GetString(bytes);
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
