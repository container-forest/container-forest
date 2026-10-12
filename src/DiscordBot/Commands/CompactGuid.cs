namespace ContainerForest.DiscordBot.Commands;

/// <summary>
/// A GUID in 22 characters of base64url instead of 36. A button's custom id holds at most 100 characters, and both
/// /dashboard and the notification buttons carry ids in it.
/// </summary>
public static class CompactGuid
{
    public const int Length = 22;

    public static string Encode(Guid value) =>
        Convert.ToBase64String(value.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads what <see cref="Encode"/> wrote; anything else is false.</summary>
    public static bool TryDecode(string text, out Guid value)
    {
        value = default;
        if (text.Length != Length)
        {
            return false;
        }

        var bytes = new byte[16];
        var base64 = text.Replace('-', '+').Replace('_', '/') + "==";
        if (!Convert.TryFromBase64String(base64, bytes, out var written) || written != 16)
        {
            return false;
        }

        value = new Guid(bytes);
        return true;
    }
}
