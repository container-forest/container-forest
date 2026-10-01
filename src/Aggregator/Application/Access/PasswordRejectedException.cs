namespace ContainerForest.Aggregator.Application.Access;

/// <summary>
/// A password was not accepted: the new one fails the password policy, or the current one given to change it
/// is wrong. Maps to HTTP 400 with <see cref="Reasons"/>; the password itself is never part of the message.
/// </summary>
public sealed class PasswordRejectedException(IReadOnlyList<string> reasons) : Exception(string.Join(" ", reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}
