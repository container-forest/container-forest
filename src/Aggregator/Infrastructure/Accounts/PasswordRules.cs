using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>
/// A check on a new password beyond the minimum length that <see cref="AccountPolicy"/> sets, after NIST SP 800-63B:
/// no rules about character classes, but no passwords an attacker tries first. Identity runs every registered rule
/// and reports all failures together.
/// </summary>
public abstract class PasswordRule : IPasswordValidator<AccountUser>
{
    /// <summary>Identity error code; it starts with "Password", which marks the error as a rejected password.</summary>
    public abstract string Code { get; }

    public Task<IdentityResult> ValidateAsync(UserManager<AccountUser> manager, AccountUser user, string? password)
    {
        var reason = Check(password ?? "", user.UserName);
        return Task.FromResult(reason is null
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = Code, Description = reason }));
    }

    /// <returns>Why the password is rejected, or null when it passes.</returns>
    protected internal abstract string? Check(string password, string? userName);
}

/// <summary>
/// NIST asks to accept at least 64 characters; the cap only keeps request bodies with megabyte "passwords" away
/// from the hashing.
/// </summary>
public sealed class MaximumLengthRule : PasswordRule
{
    public const int MaxLength = 128;

    public override string Code => "PasswordTooLong";

    protected internal override string? Check(string password, string? userName) =>
        password.Length > MaxLength ? $"Passwords must be at most {MaxLength} characters." : null;
}

/// <summary>
/// Repeated chunks ("aaaaaaaaaaaa", "abcabcabcabc"), runs of consecutive letters or digits ("123456789012",
/// "lkjihgfedcba") and keyboard rows ("qwertyuiopas"): long enough for the length rule, but guessed first.
/// </summary>
public sealed class RepetitiveOrSequentialRule : PasswordRule
{
    /// <summary>The longest chunk whose repetition still counts as repetitive.</summary>
    public const int MaxRepeatedChunk = 4;

    private const string KeyboardRows = "qwertyuiopasdfghjklzxcvbnm";

    public override string Code => "PasswordRepetitiveOrSequential";

    protected internal override string? Check(string password, string? userName)
    {
        var lower = password.ToLowerInvariant();
        return lower.Length > 0 && (IsRepetitive(lower) || IsSequential(lower) || IsKeyboardRow(lower))
            ? "Passwords must not be repeated or sequential characters, such as 'aaaa…', '1234…' or 'qwerty…'."
            : null;
    }

    private static bool IsRepetitive(string password)
    {
        for (var chunk = 1; chunk <= MaxRepeatedChunk && chunk < password.Length; chunk++)
        {
            var repeats = true;
            for (var i = chunk; i < password.Length && repeats; i++)
            {
                repeats = password[i] == password[i % chunk];
            }

            if (repeats)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every character is the next (or every one the previous) of the one before; digits wrap 9 → 0.</summary>
    private static bool IsSequential(string password)
    {
        if (password.All(char.IsAsciiDigit))
        {
            return Steps(password, (a, b) => (b - a + 10) % 10).Distinct().ToList() is [1] or [9];
        }

        return password.All(char.IsAsciiLetterLower)
            && Steps(password, (a, b) => b - a).Distinct().ToList() is [1] or [-1];
    }

    private static bool IsKeyboardRow(string password) =>
        KeyboardRows.Contains(password, StringComparison.Ordinal)
        || KeyboardRows.Contains(new string(password.Reverse().ToArray()), StringComparison.Ordinal);

    private static IEnumerable<int> Steps(string password, Func<char, char, int> step) =>
        password.Zip(password.Skip(1), step);
}

/// <summary>Words an attacker takes from the context first: the user's own name and the name of this system.</summary>
public sealed class ContextWordsRule : PasswordRule
{
    /// <summary>Shorter user names are too common inside unrelated words to reject on.</summary>
    public const int MinUserNameLength = 3;

    private static readonly string[] ServiceNames = ["container-forest", "containerforest", "container forest"];

    public override string Code => "PasswordContainsContextWord";

    protected internal override string? Check(string password, string? userName)
    {
        if (userName is { Length: >= MinUserNameLength } && password.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            return "Passwords must not contain the user name.";
        }

        return ServiceNames.Any(name => password.Contains(name, StringComparison.OrdinalIgnoreCase))
            ? "Passwords must not contain the name of this system."
            : null;
    }
}
