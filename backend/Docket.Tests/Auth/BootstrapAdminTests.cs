using Docket.Application.Auth;
using Docket.Infrastructure.Identity;
using Docket.Infrastructure.Persistence;
using Docket.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Docket.Tests.Auth;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BootstrapAdminTests
{
    private readonly ApiFixture _fixture;

    public BootstrapAdminTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Bootstrap_should_create_one_admin_and_skip_later_runs()
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";

        await _fixture.Factory.Services.EnsureBootstrapAdminAsync(
            email,
            ApiTestClient.ValidPassword,
            "Bootstrap Admin");

        using var scope = _fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(email);

        admin.Should().NotBeNull();
        (await users.GetRolesAsync(admin!)).Should().ContainSingle().Which.Should().Be(Roles.Admin);

        var adminCount = (await users.GetUsersInRoleAsync(Roles.Admin)).Count;

        await _fixture.Factory.Services.EnsureBootstrapAdminAsync(
            $"second-{Guid.NewGuid():N}@example.com",
            "Another!Passw0rd",
            "Second Admin");

        using var secondScope = _fixture.Factory.Services.CreateScope();
        var secondUsers = secondScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        (await secondUsers.GetUsersInRoleAsync(Roles.Admin)).Count.Should().Be(adminCount);
    }
}
