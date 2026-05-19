// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Loaded-extension descriptor held in <see cref="ExtensionHandlerRegistry"/>.
/// One descriptor per successfully loaded extension.
/// </summary>
public sealed record ExtensionDescriptor(
    string ExtensionId,
    string Version,
    ExtensionManifest Manifest,
    IReadOnlyList<HandlerBinding> Handlers,
    ExtensionAssemblyLoadContext? LoadContext);

/// <summary>
/// A single bound handler within a loaded extension — maps one manifest
/// <see cref="ExtensionHandler"/> to its instantiated middleware object.
/// </summary>
public sealed record HandlerBinding(
    string HandlerId,
    string Seam,
    int Priority,
    string FailureMode,
    object HandlerInstance);
