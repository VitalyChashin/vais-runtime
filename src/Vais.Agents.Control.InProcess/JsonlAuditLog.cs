// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// <see cref="IAuditLog"/> that appends every <see cref="AuditLogEntry"/> as one
/// JSON object per line (JSON Lines / NDJSON). The format the control-plane
/// reference implementations use for a durable, grep-able authoring trail — one
/// line per allowed, denied, or failed lifecycle verb.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> The shipped default is <see cref="NullAuditLog"/>; a host wires
/// this in (e.g. from a configured file path) when it wants a persisted trail.
/// </para>
/// <para>
/// <b>Swallow semantics.</b> Per the <see cref="IAuditLog"/> contract, write
/// failures must never break the lifecycle verb (it already ran or was denied by
/// the time the entry is written). Serialization / IO exceptions are caught and
/// dropped — the trail is best-effort.
/// </para>
/// </remarks>
public sealed class JsonlAuditLog : IAuditLog, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Append to <paramref name="writer"/> (caller owns its lifetime — not disposed
    /// by this instance). Useful for tests (a <see cref="StringWriter"/>) or routing
    /// to an already-open stream.
    /// </summary>
    public JsonlAuditLog(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _ownsWriter = false;
    }

    /// <summary>
    /// Append to the file at <paramref name="filePath"/> (created if absent, opened
    /// in shared-append mode). The owning <see cref="StreamWriter"/> is disposed with
    /// this instance.
    /// </summary>
    public JsonlAuditLog(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _ownsWriter = true;
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Audit-write failures must not break the verb.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
        _gate.Dispose();
    }
}
