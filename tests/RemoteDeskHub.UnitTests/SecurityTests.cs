using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.UnitTests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void HashPassword_ProducesVerifiableHash()
    {
        var (hash, salt, iterations) = PasswordHasher.HashPassword("Correct-Horse-99");

        Assert.Equal(PasswordHasher.DefaultIterations, iterations);
        Assert.NotEqual(hash, salt);
        Assert.True(PasswordHasher.VerifyPassword("Correct-Horse-99", hash, salt, iterations));
    }

    [Fact]
    public void VerifyPassword_RejectsWrongPassword()
    {
        var (hash, salt, iterations) = PasswordHasher.HashPassword("Correct-Horse-99");

        Assert.False(PasswordHasher.VerifyPassword("correct-horse-99", hash, salt, iterations));
    }

    [Fact]
    public void VerifyPassword_RejectsMalformedHash()
    {
        Assert.False(PasswordHasher.VerifyPassword("anything", "not-base64!!", "also-not-base64!!", 210_000));
    }

    [Fact]
    public void HashPassword_UsesUniqueSaltPerCall()
    {
        var first = PasswordHasher.HashPassword("SamePassword1");
        var second = PasswordHasher.HashPassword("SamePassword1");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void GenerateToken_IsUrlSafeAndUnique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => PasswordHasher.GenerateToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token =>
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
            Assert.True(token.Length >= 40);
        });
    }

    [Fact]
    public void HashToken_IsDeterministicSha256()
    {
        var first = PasswordHasher.HashToken("token-value");
        var second = PasswordHasher.HashToken("token-value");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, PasswordHasher.HashToken("token-value-2"));
    }

    [Theory]
    [InlineData("short1A", false)]
    [InlineData("alllowercase123", false)]
    [InlineData("ALLUPPERCASE123", false)]
    [InlineData("NoDigitsHereAtAll", false)]
    [InlineData("GoodPassword123", true)]
    public void IsPasswordStrong_EnforcesComplexity(string password, bool expected)
    {
        Assert.Equal(expected, PasswordHasher.IsPasswordStrong(password, out var error));
        if (!expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void MaskToken_KeepsOnlyEdges()
    {
        Assert.Equal("abcd...wxyz", PasswordHasher.MaskToken("abcdefghijklmnopwxyz"));
        Assert.Equal(string.Empty, PasswordHasher.MaskToken(string.Empty));
        Assert.Equal("***", PasswordHasher.MaskToken("abc"));
    }
}

public sealed class CredentialRulesTests
{
    [Theory]
    [InlineData("admin", true)]
    [InlineData("john.doe", true)]
    [InlineData("ops-team_1", true)]
    [InlineData("user@corp", true)]
    [InlineData("ab", false)]
    [InlineData("", false)]
    [InlineData("bad/name", false)]
    [InlineData("bad\\name", false)]
    public void IsValidUsername_EnforcesRules(string username, bool expected)
    {
        Assert.Equal(expected, CredentialRules.IsValidUsername(username, out var error));
        Assert.Equal(expected, string.IsNullOrEmpty(error));
    }

    [Fact]
    public void IsValidUsername_RejectsOverlongNames()
    {
        Assert.False(CredentialRules.IsValidUsername(new string('a', 65), out _));
    }
}

public sealed class PermissionTests
{
    [Fact]
    public void Administrator_HasEveryPermission()
    {
        Assert.All(Enum.GetValues<Permission>(), permission =>
            Assert.True(Permissions.HasPermission(UserRole.Administrator, permission)));
    }

    [Theory]
    [InlineData(Permission.ViewDashboard, true)]
    [InlineData(Permission.ViewComputers, true)]
    [InlineData(Permission.ViewMonitoring, true)]
    [InlineData(Permission.ViewReports, true)]
    [InlineData(Permission.ExportData, true)]
    [InlineData(Permission.RunCommands, false)]
    [InlineData(Permission.TransferFiles, false)]
    [InlineData(Permission.ManageUsers, false)]
    [InlineData(Permission.ManageSettings, false)]
    [InlineData(Permission.EnrollAgents, false)]
    [InlineData(Permission.ApproveCommands, false)]
    public void Viewer_IsReadOnly(Permission permission, bool expected)
    {
        Assert.Equal(expected, Permissions.HasPermission(UserRole.Viewer, permission));
    }

    [Theory]
    [InlineData(Permission.RunCommands, true)]
    [InlineData(Permission.ManageProcesses, true)]
    [InlineData(Permission.ManageServices, true)]
    [InlineData(Permission.TransferFiles, true)]
    [InlineData(Permission.ViewAudit, true)]
    [InlineData(Permission.ManageUsers, false)]
    [InlineData(Permission.ManageSettings, false)]
    [InlineData(Permission.ManageComputers, false)]
    [InlineData(Permission.BackupDatabase, false)]
    [InlineData(Permission.ApproveCommands, false)]
    public void Operator_HasDayToDayPermissions(Permission permission, bool expected)
    {
        Assert.Equal(expected, Permissions.HasPermission(UserRole.Operator, permission));
    }

    [Fact]
    public void SessionUser_Can_DelegatesToPermissions()
    {
        var viewer = new SessionUser(Guid.NewGuid(), "viewer", UserRole.Viewer, DateTime.UtcNow);

        Assert.True(viewer.Can(Permission.ViewDashboard));
        Assert.False(viewer.Can(Permission.ManageUsers));
    }

    [Fact]
    public void SessionContext_SignInAndOut()
    {
        var context = new SessionContext();
        Assert.False(context.IsAuthenticated);

        context.SignIn(new SessionUser(Guid.NewGuid(), "admin", UserRole.Administrator, DateTime.UtcNow));
        Assert.True(context.IsAuthenticated);
        Assert.Equal("admin", context.CurrentUser!.UserName);

        context.SignOut();
        Assert.False(context.IsAuthenticated);
        Assert.Null(context.CurrentUser);
    }

    [Fact]
    public void Describe_ReturnsRoleText()
    {
        Assert.Contains("Full control", Permissions.Describe(UserRole.Administrator));
        Assert.Contains("Read-only", Permissions.Describe(UserRole.Viewer));
    }
}
