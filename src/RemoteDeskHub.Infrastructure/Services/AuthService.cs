using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class AuthService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAuditService audit,
    ISessionContext session,
    IClock clock) : IAuthService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    public async Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserAccounts.AnyAsync(cancellationToken);
    }

    public async Task<OperationResult<SessionUser>> CreateInitialAdministratorAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        username = (username ?? string.Empty).Trim();
        if (!IsValidUsername(username, out var usernameError))
        {
            return OperationResult<SessionUser>.Fail(usernameError);
        }

        if (!PasswordHasher.IsPasswordStrong(password, out var passwordError))
        {
            return OperationResult<SessionUser>.Fail(passwordError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.UserAccounts.AnyAsync(cancellationToken))
        {
            return OperationResult<SessionUser>.Fail("An administrator account already exists.");
        }

        var (hash, salt, iterations) = PasswordHasher.HashPassword(password);
        var user = new UserAccount
        {
            Username = username,
            PasswordHash = hash,
            PasswordSalt = salt,
            Iterations = iterations,
            Role = UserRole.Administrator,
            IsEnabled = true,
            CreatedUtc = clock.UtcNow
        };
        db.UserAccounts.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(username, AuditOperations.UserCreated, result: "Success",
            details: "Initial administrator account created.", cancellationToken: cancellationToken);

        return OperationResult<SessionUser>.Ok(new SessionUser(user.Id, user.Username, user.Role, clock.UtcNow));
    }

    public async Task<OperationResult<SessionUser>> LoginAsync(string username, string password, string? remoteAddress, CancellationToken cancellationToken = default)
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return OperationResult<SessionUser>.Fail("Enter a user name and password.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user is null)
        {
            await audit.LogAsync(username, AuditOperations.LoginFailed, result: "Failed",
                error: "Unknown user name.", ipAddress: remoteAddress, cancellationToken: cancellationToken);
            return OperationResult<SessionUser>.Fail("Invalid user name or password.");
        }

        if (!user.IsEnabled)
        {
            await audit.LogAsync(user.Username, AuditOperations.LoginFailed, result: "Failed",
                error: "Account disabled.", ipAddress: remoteAddress, cancellationToken: cancellationToken);
            return OperationResult<SessionUser>.Fail("This account has been disabled.");
        }

        if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc.Value > clock.UtcNow)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((user.LockoutUntilUtc.Value - clock.UtcNow).TotalMinutes));
            await audit.LogAsync(user.Username, AuditOperations.LoginFailed, result: "Failed",
                error: "Account locked.", ipAddress: remoteAddress, cancellationToken: cancellationToken);
            return OperationResult<SessionUser>.Fail($"Too many failed attempts. Try again in {minutes} minute(s).");
        }

        if (!PasswordHasher.VerifyPassword(password, user.PasswordHash, user.PasswordSalt, user.Iterations))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockoutUntilUtc = clock.UtcNow.Add(LockoutDuration);
                user.FailedLoginCount = 0;
            }

            await db.SaveChangesAsync(cancellationToken);
            await audit.LogAsync(user.Username, AuditOperations.LoginFailed, result: "Failed",
                error: "Invalid password.", ipAddress: remoteAddress, cancellationToken: cancellationToken);
            return OperationResult<SessionUser>.Fail("Invalid user name or password.");
        }

        user.FailedLoginCount = 0;
        user.LockoutUntilUtc = null;
        user.LastLoginUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(user.Username, AuditOperations.Login, result: "Success",
            ipAddress: remoteAddress, cancellationToken: cancellationToken);

        return OperationResult<SessionUser>.Ok(new SessionUser(user.Id, user.Username, user.Role, clock.UtcNow));
    }

    public async Task<OperationResult> CreateUserAsync(string actor, string username, string password, UserRole role, CancellationToken cancellationToken = default)
    {
        if (await HasAnyUserAsync(cancellationToken))
        {
            AccessGuard.Require(session, Permission.ManageUsers, actor);
        }

        username = (username ?? string.Empty).Trim();
        if (!IsValidUsername(username, out var usernameError))
        {
            return OperationResult.Fail(usernameError);
        }

        if (!PasswordHasher.IsPasswordStrong(password, out var passwordError))
        {
            return OperationResult.Fail(passwordError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.UserAccounts.AnyAsync(u => u.Username == username, cancellationToken))
        {
            return OperationResult.Fail("A user with that name already exists.");
        }

        var (hash, salt, iterations) = PasswordHasher.HashPassword(password);
        db.UserAccounts.Add(new UserAccount
        {
            Username = username,
            PasswordHash = hash,
            PasswordSalt = salt,
            Iterations = iterations,
            Role = role,
            IsEnabled = true,
            CreatedUtc = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.UserCreated, details: $"User '{username}' created with role {role}.", cancellationToken: cancellationToken);
        return OperationResult.Ok($"User '{username}' created.");
    }

    public async Task<OperationResult> SetUserEnabledAsync(string actor, Guid userId, bool enabled, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageUsers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return OperationResult.Fail("User not found.");
        }

        if (!enabled && user.Role == UserRole.Administrator &&
            await db.UserAccounts.CountAsync(u => u.Role == UserRole.Administrator && u.IsEnabled, cancellationToken) <= 1)
        {
            return OperationResult.Fail("The last enabled administrator cannot be disabled.");
        }

        user.IsEnabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Security.UserEnabledChanged", details: $"User '{user.Username}' enabled={enabled}.", cancellationToken: cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> ChangeRoleAsync(string actor, Guid userId, UserRole role, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageUsers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return OperationResult.Fail("User not found.");
        }

        if (user.Role == UserRole.Administrator && role != UserRole.Administrator &&
            await db.UserAccounts.CountAsync(u => u.Role == UserRole.Administrator && u.IsEnabled, cancellationToken) <= 1)
        {
            return OperationResult.Fail("The last enabled administrator cannot lose the administrator role.");
        }

        user.Role = role;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Security.UserRoleChanged", details: $"User '{user.Username}' role changed to {role}.", cancellationToken: cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> ResetPasswordAsync(string actor, Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageUsers, actor);
        if (!PasswordHasher.IsPasswordStrong(newPassword, out var error))
        {
            return OperationResult.Fail(error);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return OperationResult.Fail("User not found.");
        }

        var (hash, salt, iterations) = PasswordHasher.HashPassword(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.Iterations = iterations;
        user.FailedLoginCount = 0;
        user.LockoutUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Security.PasswordReset", details: $"Password reset for '{user.Username}'.", cancellationToken: cancellationToken);
        return OperationResult.Ok("Password updated.");
    }

    public async Task<OperationResult> ChangePasswordAsync(string username, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user is null || !PasswordHasher.VerifyPassword(currentPassword, user.PasswordHash, user.PasswordSalt, user.Iterations))
        {
            return OperationResult.Fail("The current password is not correct.");
        }

        if (!PasswordHasher.IsPasswordStrong(newPassword, out var error))
        {
            return OperationResult.Fail(error);
        }

        var (hash, salt, iterations) = PasswordHasher.HashPassword(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.Iterations = iterations;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(user.Username, "Security.PasswordChanged", cancellationToken: cancellationToken);
        return OperationResult.Ok("Password changed.");
    }

    public async Task<IReadOnlyList<UserAccountView>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageUsers);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserAccounts.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserAccountView
            {
                Id = u.Id,
                Username = u.Username,
                Role = u.Role,
                IsEnabled = u.IsEnabled,
                CreatedUtc = u.CreatedUtc,
                LastLoginUtc = u.LastLoginUtc,
                FailedLoginCount = u.FailedLoginCount,
                LockoutUntilUtc = u.LockoutUntilUtc
            })
            .ToListAsync(cancellationToken);
    }

    public static bool IsValidUsername(string username, out string error) =>
        CredentialRules.IsValidUsername(username, out error);
}
