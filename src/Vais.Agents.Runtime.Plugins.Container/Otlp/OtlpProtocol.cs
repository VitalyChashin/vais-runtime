// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Google.Protobuf;

namespace Vais.Agents.Runtime.Plugins.Container.Otlp;

/// <summary>Minimal OTLP span extracted from an ExportTraceServiceRequest.</summary>
internal sealed class OtlpSpan
{
    public ByteString TraceId { get; set; } = ByteString.Empty;
    public ByteString SpanId { get; set; } = ByteString.Empty;
    public ByteString ParentSpanId { get; set; } = ByteString.Empty;
    public string Name { get; set; } = string.Empty;
    public int Kind { get; set; }
    public ulong StartTimeUnixNano { get; set; }
    public ulong EndTimeUnixNano { get; set; }
    public List<(string Key, string Value)> Attributes { get; } = [];
}

/// <summary>
/// Minimal parser for OTLP ExportTraceServiceRequest protobuf binary.
/// Reads only the fields needed to re-emit spans as .NET Activity objects.
/// Field numbering follows opentelemetry-proto trace/v1/trace.proto.
/// </summary>
internal static class OtlpTraceParser
{
    // ExportTraceServiceRequest field numbers
    private const int FieldResourceSpans = 1;

    // ResourceSpans field numbers
    private const int FieldScopeSpans = 2;

    // ScopeSpans field numbers
    private const int FieldSpans = 2;

    // Span field numbers
    private const int FieldTraceId        = 1;
    private const int FieldSpanId         = 2;
    private const int FieldParentSpanId   = 4;
    private const int FieldName           = 5;
    private const int FieldKind           = 6;
    private const int FieldStartTimeNano  = 7;
    private const int FieldEndTimeNano    = 8;
    private const int FieldAttributes     = 9;

    // KeyValue / AnyValue field numbers
    private const int FieldKvKey   = 1;
    private const int FieldKvValue = 2;
    private const int FieldAnyStringValue = 1;

    public static List<OtlpSpan> ParseExportRequest(byte[] data)
    {
        var spans = new List<OtlpSpan>();
        var input = new CodedInputStream(data);
        ParseResourceSpansList(input, spans);
        return spans;
    }

    private static void ParseResourceSpansList(CodedInputStream input, List<OtlpSpan> spans)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);

            if (fieldNumber == FieldResourceSpans && wireType == WireFormat.WireType.LengthDelimited)
            {
                var bytes = input.ReadBytes();
                ParseScopeSpansList(new CodedInputStream(bytes.ToByteArray()), spans);
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static void ParseScopeSpansList(CodedInputStream input, List<OtlpSpan> spans)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);

            if (fieldNumber == FieldScopeSpans && wireType == WireFormat.WireType.LengthDelimited)
            {
                var bytes = input.ReadBytes();
                ParseSpansList(new CodedInputStream(bytes.ToByteArray()), spans);
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static void ParseSpansList(CodedInputStream input, List<OtlpSpan> spans)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);

            if (fieldNumber == FieldSpans && wireType == WireFormat.WireType.LengthDelimited)
            {
                var bytes = input.ReadBytes();
                var span = ParseSpan(new CodedInputStream(bytes.ToByteArray()));
                spans.Add(span);
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static OtlpSpan ParseSpan(CodedInputStream input)
    {
        var span = new OtlpSpan();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);

            switch (fieldNumber)
            {
                case FieldTraceId when wireType == WireFormat.WireType.LengthDelimited:
                    span.TraceId = input.ReadBytes();
                    break;
                case FieldSpanId when wireType == WireFormat.WireType.LengthDelimited:
                    span.SpanId = input.ReadBytes();
                    break;
                case FieldParentSpanId when wireType == WireFormat.WireType.LengthDelimited:
                    span.ParentSpanId = input.ReadBytes();
                    break;
                case FieldName when wireType == WireFormat.WireType.LengthDelimited:
                    span.Name = input.ReadString();
                    break;
                case FieldKind when wireType == WireFormat.WireType.Varint:
                    span.Kind = input.ReadInt32();
                    break;
                case FieldStartTimeNano when wireType == WireFormat.WireType.Fixed64:
                    span.StartTimeUnixNano = input.ReadFixed64();
                    break;
                case FieldEndTimeNano when wireType == WireFormat.WireType.Fixed64:
                    span.EndTimeUnixNano = input.ReadFixed64();
                    break;
                case FieldAttributes when wireType == WireFormat.WireType.LengthDelimited:
                    var kvBytes = input.ReadBytes();
                    if (ParseKeyValue(new CodedInputStream(kvBytes.ToByteArray())) is { } kv)
                        span.Attributes.Add(kv);
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return span;
    }

    private static (string Key, string Value)? ParseKeyValue(CodedInputStream input)
    {
        string? key = null;
        string? value = null;
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);

            switch (fieldNumber)
            {
                case FieldKvKey when wireType == WireFormat.WireType.LengthDelimited:
                    key = input.ReadString();
                    break;
                case FieldKvValue when wireType == WireFormat.WireType.LengthDelimited:
                    value = ParseAnyValue(new CodedInputStream(input.ReadBytes().ToByteArray()));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return key is not null ? (key, value ?? string.Empty) : null;
    }

    private static string ParseAnyValue(CodedInputStream input)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);

            if (fieldNumber == FieldAnyStringValue && wireType == WireFormat.WireType.LengthDelimited)
                return input.ReadString();

            input.SkipLastField();
        }
        return string.Empty;
    }
}
