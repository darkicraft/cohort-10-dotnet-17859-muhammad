using Docket.Application.Auth;
using Docket.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Docket.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies migrations when asked, seeds the roles, and creates the first admin when no
    /// admin exists. The bootstrap credentials are read once and can be removed afterwards.
    /// </summary>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        bool migrate,
        IConfiguration configuration,
        bool isProduction,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        if (migrate)
        {
            var database = scope.ServiceProvider.GetRequiredService<DocketDbContext>();
            await database.Database.MigrateAsync(cancellationToken);
        }

        await EnsureRolesAsync(scope.ServiceProvider, cancellationToken);
        await EnsureBootstrapAdminFromConfigurationAsync(
            scope.ServiceProvider,
            configuration,
            isProduction,
            logger,
            cancellationToken);
    }

    /// <summary>
    /// Creates or promotes the first admin. Does nothing when an admin already exists.
    /// Exposed for the bootstrap test; the application calls it through configuration.
    /// </summary>
    public static async Task EnsureBootstrapAdminAsync(
        this IServiceProvider services,
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await CreateBootstrapAdminAsync(
            scope.ServiceProvider,
            email.Trim(),
            password,
            displayName.Trim(),
            cancellationToken);
    }

    private static async Task EnsureBootstrapAdminFromConfigurationAsync(
        IServiceProvider services,
        IConfiguration configuration,
        bool isProduction,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if ((await userManager.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            return;
        }

        var email = configuration["Bootstrap:AdminEmail"];
        var password = configuration["Bootstrap:AdminPassword"];
        var displayName = configuration["Bootstrap:AdminDisplayName"] ?? "Docket Admin";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            if (isProduction)
            {
                throw new InvalidOperationException(
                    "No Admin user exists. Set Bootstrap__AdminEmail and Bootstrap__AdminPassword for the first deploy.");
            }

            logger.LogWarning("No Admin user exists and no bootstrap credentials are configured.");
            return;
        }

        await CreateBootstrapAdminAsync(
            services,
            email.Trim(),
            password,
            displayName.Trim(),
            cancellationToken);

        logger.LogInformation(
            "Bootstrap admin is ready. Remove the bootstrap credentials from the environment.");
    }

    private static async Task CreateBootstrapAdminAsync(
        IServiceProvider services,
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if ((await userManager.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            return;
        }

        var database = services.GetRequiredService<DocketDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var strategy = database.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    Email = email,
                    UserName = email,
                    DisplayName = displayName,
                    CreatedAtUtc = clock.GetUtcNow()
                };

                var created = await userManager.CreateAsync(user, password);
                if (!created.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not create the bootstrap admin: {string.Join("; ", created.Errors.Select(error => error.Description))}");
                }
            }

            if (await userManager.IsInRoleAsync(user, Roles.User))
            {
                var removed = await userManager.RemoveFromRoleAsync(user, Roles.User);
                if (!removed.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not remove the '{Roles.User}' role from the bootstrap admin: {string.Join("; ", removed.Errors.Select(error => error.Description))}");
                }
            }

            if (!await userManager.IsInRoleAsync(user, Roles.Admin))
            {
                var assigned = await userManager.AddToRoleAsync(user, Roles.Admin);
                if (!assigned.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not assign the '{Roles.Admin}' role: {string.Join("; ", assigned.Errors.Select(error => error.Description))}");
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    private static async Task EnsureRolesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var role in Roles.All)
        {
            if (await roleManager.RoleExistsAsync(role))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new IdentityRole<Guid>(role));
            if (result.Succeeded || await roleManager.RoleExistsAsync(role))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Could not create the '{role}' role: {string.Join("; ", result.Errors.Select(error => error.Description))}");
        }
    }
}
