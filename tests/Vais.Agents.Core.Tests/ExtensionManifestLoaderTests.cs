// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// MS-1c-ext guard — the JSON loader's <c>ParseExtension</c> must parse the same
/// <c>ExtensionSpec</c> fields the server YAML deserializer
/// (<c>ExtensionManifestYamlDeserializer</c>) does, closing the divergent-parser gap
/// (<c>plans/gaps/extension-manifest-parse-completeness-gap-2026-05-23.md</c>). Previously
/// the JSON loader dropped the container fields, handler timeout, and secrets.
/// </summary>
public sealed class ExtensionManifestLoaderTests
{
    [Fact]
    public async Task ParseExtension_ParsesContainerFields_HandlerTimeout_AndSecrets()
    {
        const string json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "Extension",
              "metadata": { "name": "container-ext", "version": "2.0" },
              "spec": {
                "host": "container",
                "image": "my/ext:1",
                "port": 9000,
                "topology": "kubernetes",
                "startupTimeoutSeconds": 45,
                "invokeTimeoutSeconds": 10,
                "imagePullPolicy": "Always",
                "handlers": [ { "id": "h1", "seam": "toolGatewayMiddleware", "timeoutSeconds": 7 } ],
                "secrets": { "TOKEN": "secret://env/TOK" }
              }
            }
            """;

        var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(json);
        var ext = ((ManifestResource.ExtensionCase)resources.Single()).Extension;

        ext.Id.Should().Be("container-ext");
        ext.Spec.Host.Should().Be("container");
        ext.Spec.Image.Should().Be("my/ext:1");
        ext.Spec.Port.Should().Be(9000);
        ext.Spec.Topology.Should().Be("kubernetes");
        ext.Spec.StartupTimeoutSeconds.Should().Be(45);
        ext.Spec.InvokeTimeoutSeconds.Should().Be(10);
        ext.Spec.ImagePullPolicy.Should().Be("Always");
        ext.Spec.Handlers.Single().TimeoutSeconds.Should().Be(7);
        ext.Spec.Secrets.Should().ContainKey("TOKEN").WhoseValue.Should().Be("secret://env/TOK");
    }
}
