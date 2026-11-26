using System.Security.Cryptography;
using System.Text;
using Docket.Application.Auth;
using Docket.Infrastructure.Identity;
using Docket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docket.Infrastructure.Auth;

public sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    DocketDbContext database,
    JwtTokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider clock) : IAuthService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var strategy = database.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

            var email = request.Email.Trim();
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserName = email,
                DisplayName = request.DisplayName.Trim(),
                CreatedAtUtc = clock.GetUtcNow()
            };

            var created = await userManager.CreateAsync(user, request.Password);
            if (!created.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);

                if (created.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName"))
                {
                    return AuthResult.Failure(409, "email_taken", "An account with this email already exists.");
                }

                return AuthResult.Failure(
                    400,
                    "validation_error",
                    string.Join(" ", created.Errors.Select(error => error.Description)));
            }

            var assigned = await userManager.AddToRoleAsync(user, Roles.User);
            if (!assigned.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Could not assign the '{Roles.User}' role: {string.Join("; ", assigned.Errors.Select(error => error.Description))}");
            }

            var result = await IssueAsync(user, [Roles.User], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            return InvalidCredentials();
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return InvalidCredentials();
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);
            return InvalidCredentials();
        }

        await userManager.ResetAccessFailedCountAsync(user);

        var roles = await userManager.GetRolesAsync(user);
        return await IssueAsync(user, roles, cancellationToken);
    }

    public async Task<AuthResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return InvalidRefreshToken();
        }

        var now = clock.GetUtcNow();
        var hash = Hash(refreshToken);
        var stored = await database.RefreshTokens
            .Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            return InvalidRefreshToken();
        }

        if (stored.FamilyId == Guid.Empty)
        {
            // A token without a family can only come from an old writer. Reject and revoke it.
            if (stored.RevokedAtUtc is null)
            {
                stored.RevokedAtUtc = now;
                await database.SaveChangesAsync(cancellationToken);
            }

            return InvalidRefreshToken();
        }

        if (stored.RevokedAtUtc is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            return InvalidRefreshToken();
        }

        if (now >= stored.ExpiresAtUtc)
        {
            return InvalidRefreshToken();
        }

        stored.RevokedAtUtc = now;
        var (rawToken, replacement) = CreateRefreshToken(stored.UserId, now, stored.FamilyId);
        stored.ReplacedByTokenHash = replacement.TokenHash;
        database.RefreshTokens.Add(replacement);

        var roles = await userManager.GetRolesAsync(stored.User);
        var access = tokenService.Create(stored.User, roles);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request claimed this token first. Its replacement is the only valid one.
            return InvalidRefreshToken();
        }

        return AuthResult.Success(
            new AuthResponse(access.Value, access.ExpiresAtUtc, Map(stored.User, roles)),
            rawToken);
    }

    public async Task<bool> LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        var hash = Hash(refreshToken);
        var stored = await database.RefreshTokens
            .SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            return false;
        }

        stored.RevokedAtUtc ??= clock.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return null;
        }

        var roles = await userManager.GetRolesAsync(user);
        return Map(user, roles);
    }

    private async Task<AuthResult> IssueAsync(
        ApplicationUser user,
        IList<string> roles,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var access = tokenService.Create(user, roles);
        var (rawToken, entity) = CreateRefreshToken(user.Id, now);

        database.RefreshTokens.Add(entity);
        await database.SaveChangesAsync(cancellationToken);

        return AuthResult.Success(
            new AuthResponse(access.Value, access.ExpiresAtUtc, Map(user, roles)),
            rawToken);
    }

    private (string RawToken, RefreshToken Entity) CreateRefreshToken(
        Guid userId,
        DateTimeOffset now,
        Guid? familyId = null)
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId ?? Guid.NewGuid(),
            TokenHash = Hash(rawToken),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_jwt.RefreshTokenDays)
        };

        return (rawToken, entity);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (familyId == Guid.Empty)
        {
            return;
        }

        // ExecuteUpdate is atomic and bypasses rowversion conflicts from concurrent rotations.
        await database.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAtUtc, now),
                cancellationToken);
    }

    private static AuthResult InvalidCredentials()
        => AuthResult.Failure(401, "invalid_credentials", "The email or password is incorrect.");

    private static AuthResult InvalidRefreshToken()
        => AuthResult.Failure(401, "invalid_refresh_token", "The refresh token is invalid or expired.");

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static UserProfile Map(ApplicationUser user, IEnumerable<string> roles)
        => new(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            roles.OrderBy(role => role, StringComparer.Ordinal).ToArray());
}
