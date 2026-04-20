// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Cli;

/// <summary>
/// Implements the curl-style <c>@file</c> convention: arguments
/// prefixed with <c>@</c> are resolved by reading the remainder as a
/// filesystem path. Shared between <c>invoke --text</c> and
/// <c>signal --payload</c>.
/// </summary>
internal static class ArgumentFileReader
{
    /// <summary>
    /// If <paramref name="value"/> starts with <c>@</c>, read the file
    /// at the remaining path and return its contents; otherwise return
    /// <paramref name="value"/> unchanged. Null / empty pass-through.
    /// </summary>
    /// <exception cref="FileNotFoundException">Raised when <paramref name="value"/> points at a missing file.</exception>
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@')
        {
            return value;
        }
        var path = value[1..];
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Argument file not found: {path}", path);
        }
        return File.ReadAllText(path);
    }
}
