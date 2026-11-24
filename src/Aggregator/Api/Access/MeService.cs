using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Domain.Access;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>The signed-in user as the panel needs them, with their two-factor state.</summary>
internal sealed class MeService(TwoFactorManagement twoFactor)
{
    public async Task<MeResponse> DescribeAsync(User user, CancellationToken cancellationToken) => new(
        user.Id.Value,
        user.UserName,
        user.IsAdmin,
        await twoFactor.IsEnabledAsync(user.Id),
        await twoFactor.RequiresSetupAsync(user, cancellationToken));
}
