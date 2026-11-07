using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    public async Task<IReadOnlyList<UserResponse>> ListUsersAsync(CancellationToken cancellationToken) =>
        await Api.Users.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <summary>A user by name (case-insensitive) or id.</summary>
    /// <exception cref="CliException">There is no such user.</exception>
    public async Task<UserResponse> FindUserAsync(string nameOrId, CancellationToken cancellationToken)
    {
        var users = await ListUsersAsync(cancellationToken);
        return users.FirstOrDefault(u => u.Id?.ToString() == nameOrId || string.Equals(u.UserName, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"There is no user '{nameOrId}'; see 'forest user list'.", ExitCodes.Denied);
    }

    public async Task<UserDetailsResponse> GetUserAsync(Guid id, CancellationToken cancellationToken) =>
        Required(await Api.Users[id].GetAsync(cancellationToken: cancellationToken), "user");

    public async Task<UserResponse> CreateUserAsync(string userName, string password, bool isAdmin, CancellationToken cancellationToken) =>
        Required(
            await Api.Users.PostAsync(
                new CreateUserRequest { UserName = userName, Password = password, IsAdmin = isAdmin }, cancellationToken: cancellationToken),
            "user");

    public async Task RenameUserAsync(Guid id, string userName, CancellationToken cancellationToken) =>
        await Api.Users[id].UserName.PutAsync(new RenameUserRequest { UserName = userName }, cancellationToken: cancellationToken);

    public async Task SetUserActiveAsync(Guid id, bool active, CancellationToken cancellationToken)
    {
        if (active)
        {
            await Api.Users[id].Activate.PostAsync(cancellationToken: cancellationToken);
        }
        else
        {
            await Api.Users[id].Deactivate.PostAsync(cancellationToken: cancellationToken);
        }
    }

    public async Task SetUserAdminAsync(Guid id, bool isAdmin, CancellationToken cancellationToken) =>
        await Api.Users[id].Admin.PutAsync(new SetAdminRequest { IsAdmin = isAdmin }, cancellationToken: cancellationToken);

    public Task ResetUserPasswordAsync(Guid id, string password, CancellationToken cancellationToken) =>
        Api.Users[id].Password.PostAsync(new ResetPasswordRequest { NewPassword = password }, cancellationToken: cancellationToken);

    public async Task UnlockUserAsync(Guid id, CancellationToken cancellationToken) =>
        await Api.Users[id].Unlock.PostAsync(cancellationToken: cancellationToken);

    /// <param name="stackId">Null for a role on the whole environment.</param>
    public async Task<RoleAssignmentResponse> GrantRoleAsync(
        Guid userId, Guid environmentId, Guid? stackId, Role role, CancellationToken cancellationToken) =>
        Required(
            await Api.Users[userId].RoleAssignments.PostAsync(
                new GrantRoleRequest { EnvironmentId = environmentId, StackId = stackId, Role = role }, cancellationToken: cancellationToken),
            "role assignment");

    public async Task<RoleAssignmentResponse> ChangeRoleAsync(Guid userId, Guid assignmentId, Role role, CancellationToken cancellationToken) =>
        Required(
            await Api.Users[userId].RoleAssignments[assignmentId].PutAsync(new ChangeRoleRequest { Role = role }, cancellationToken: cancellationToken),
            "role assignment");

    public Task RevokeRoleAsync(Guid userId, Guid assignmentId, CancellationToken cancellationToken) =>
        Api.Users[userId].RoleAssignments[assignmentId].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<ApiTokenResponse>> ListUserTokensAsync(Guid userId, CancellationToken cancellationToken) =>
        await Api.Users[userId].Tokens.GetAsync(cancellationToken: cancellationToken) ?? [];

    public Task RevokeUserTokenAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken) =>
        Api.Users[userId].Tokens[tokenId].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<PasskeyResponse>> ListUserPasskeysAsync(Guid userId, CancellationToken cancellationToken) =>
        await Api.Users[userId].Passkeys.GetAsync(cancellationToken: cancellationToken) ?? [];

    public Task RemoveUserPasskeyAsync(Guid userId, string passkeyId, CancellationToken cancellationToken) =>
        Api.Users[userId].Passkeys[passkeyId].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<SigningKeyResponse>> ListUserSigningKeysAsync(Guid userId, CancellationToken cancellationToken) =>
        await Api.Users[userId].SigningKeys.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <param name="environmentIds">Null for every environment.</param>
    public async Task<SigningKeyResponse> AddUserSigningKeyAsync(
        Guid userId, string name, string publicKey, List<Guid?>? environmentIds, CancellationToken cancellationToken) =>
        Required(
            await Api.Users[userId].SigningKeys.PostAsync(
                new AddSigningKeyRequest { Name = name, PublicKey = publicKey, EnvironmentIds = environmentIds },
                cancellationToken: cancellationToken),
            "signing key");

    public Task RemoveUserSigningKeyAsync(Guid userId, Guid keyId, CancellationToken cancellationToken) =>
        Api.Users[userId].SigningKeys[keyId].DeleteAsync(cancellationToken: cancellationToken);
}
