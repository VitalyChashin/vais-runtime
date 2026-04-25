// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Claims;
using FluentAssertions;
using Vais.Agents.Control.Http;
using Xunit;

namespace Vais.Agents.Runtime.Host.Tests;

public sealed class ServiceAccountPrincipalMapperTests
{
    private readonly ServiceAccountPrincipalMapper _mapper = new();

    [Fact]
    public void Map_NullUser_ThrowsArgumentNullException()
    {
        var act = () => _mapper.Map(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Map_MissingSubClaim_ReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([]));
        var result = _mapper.Map(principal);
        result.Should().BeNull();
    }

    [Fact]
    public void Map_ServiceAccountSub_ExtractsTenantFromNamespace()
    {
        var principal = BuildPrincipal("sub", "system:serviceaccount:production:my-agent");

        var result = _mapper.Map(principal);

        result.Should().NotBeNull();
        result!.Id.Should().Be("system:serviceaccount:production:my-agent");
        result.TenantId.Should().Be("production");
        result.Scopes.Should().BeNull();
    }

    [Fact]
    public void Map_ServiceAccountSub_ExtractsScopesWhenPresent()
    {
        var claims = new[]
        {
            new Claim("sub", "system:serviceaccount:staging:worker"),
            new Claim("scope", "invoke:agents read:manifests"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = _mapper.Map(principal);

        result!.TenantId.Should().Be("staging");
        result.Scopes.Should().BeEquivalentTo(["invoke:agents", "read:manifests"]);
    }

    [Fact]
    public void Map_NonServiceAccountSub_UsesSubAsIdAndTenantIdClaim()
    {
        var claims = new[]
        {
            new Claim("sub", "user@example.com"),
            new Claim("tenant_id", "tenant-42"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = _mapper.Map(principal);

        result!.Id.Should().Be("user@example.com");
        result.TenantId.Should().Be("tenant-42");
    }

    [Fact]
    public void Map_NonServiceAccountSub_NoTenantIdClaim_TenantIdIsNull()
    {
        var principal = BuildPrincipal("sub", "plain-subject");

        var result = _mapper.Map(principal);

        result!.Id.Should().Be("plain-subject");
        result.TenantId.Should().BeNull();
    }

    [Fact]
    public void Map_ServiceAccountSubMissingColon_FallsBackToDefault()
    {
        // "system:serviceaccount:no-trailing-colon" — missing the <sa> segment
        var principal = BuildPrincipal("sub", "system:serviceaccount:no-sa");

        var result = _mapper.Map(principal);

        result!.Id.Should().Be("system:serviceaccount:no-sa");
        result.TenantId.Should().BeNull(
            because: "the SA format requires both a namespace and a serviceaccount segment");
    }

    [Fact]
    public void Map_NameIdentifierClaimFallback_UsedWhenSubAbsent()
    {
        var principal = BuildPrincipal(ClaimTypes.NameIdentifier, "system:serviceaccount:dev:runner");

        var result = _mapper.Map(principal);

        result!.Id.Should().Be("system:serviceaccount:dev:runner");
        result.TenantId.Should().Be("dev");
    }

    private static ClaimsPrincipal BuildPrincipal(string claimType, string value)
    {
        var claims = new[] { new Claim(claimType, value) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims));
    }
}
