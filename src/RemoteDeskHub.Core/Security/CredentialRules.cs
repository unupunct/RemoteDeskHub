namespace RemoteDeskHub.Core.Security;

/// <summary>Shared validation rules for account names. Used by setup, user management and sign-in.</summary>
public static class CredentialRules
{
    public static bool IsValidUsername(string? username, out string error)
    {
        error = string.Empty;
        username ??= string.Empty;

        if (username.Length is < 3 or > 64)
        {
            error = "User name must be between 3 and 64 characters.";
            return false;
        }

        if (!username.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@' or ' '))
        {
            error = "User name may contain letters, digits, spaces, '.', '-', '_' and '@'.";
            return false;
        }

        return true;
    }
}
