using System.CommandLine;
using ContainerForest.Cli.Api;

namespace ContainerForest.Cli.Commands;

/// <summary>The command tree of <c>forest</c>.</summary>
internal static class ForestCli
{
    public static RootCommand Build(CliHost host)
    {
        var root = new RootCommand("forest: the container-forest command line. Sign in with 'forest login <server>'.");
        foreach (var option in GlobalOptions.All)
        {
            root.Add(option);
        }

        ICliNode[] tree =
        [
            new LoginCommand(),
            new LogoutCommand(),
            new WhoAmICommand(),
            new CommandGroup("context", "Saved servers and tokens.",
                new ContextListCommand(),
                new ContextUseCommand(),
                new ContextRemoveCommand(),
                new ContextSetEnvironmentCommand()),
            new CommandGroup("env", "Environments: hosts with an agent.",
                new EnvListCommand(),
                new EnvShowCommand(),
                new EnvCreateCommand(),
                new EnvUpdateCommand(),
                new EnvRemoveCommand(),
                new EnvEnrollmentTokenCommand(),
                new EnvRevokeAgentCommand(),
                new CommandGroup("key", "Age keys SOPS files are encrypted for.",
                    new KeyListCommand(),
                    new KeyCreateCommand(),
                    new KeyRemoveCommand()),
                new CommandGroup("registry", "Container registry logins the agent pulls with.",
                    new RegistryListCommand(),
                    new RegistrySetCommand(),
                    new RegistryRemoveCommand())),
            new CommandGroup("container", "Containers of an environment.",
                new ContainerListCommand(),
                new ContainerControlCommand(ContainerAction.Start, "Start a container."),
                new ContainerControlCommand(ContainerAction.Stop, "Stop a container."),
                new ContainerControlCommand(ContainerAction.Restart, "Restart a container."),
                new ContainerLogsCommand()),
            new CommandGroup("stack", "Stacks: compose projects deployed from git.",
                new StackListCommand(),
                new StackShowCommand(),
                new StackCreateCommand(),
                new StackSetSourceCommand(),
                new StackRemoveCommand(),
                new StackDeployCommand(),
                new StackControlCommand(StackAction.Restart, "Restart the stack's containers (compose restart, Operator)."),
                new StackControlCommand(StackAction.Down, "Stop and remove the stack's containers (compose down, Editor)."),
                new StackSwitchCommand("deploy-on-push", "Deploy after a push to the branch (Editor).", (api, id, on, ct) => api.SetDeployOnPushAsync(id, on, ct)),
                new StackSwitchCommand("restart-on-file-change", "Restart after a deploy that changed files (Editor).", (api, id, on, ct) => api.SetRestartOnFileChangeAsync(id, on, ct)),
                new CommandGroup("hook", "The stack's deploy hook for CI.",
                    new StackHookCreateCommand(),
                    new StackHookRemoveCommand())),
            new CommandGroup("deployment", "Deployments of a stack.",
                new DeploymentListCommand(),
                new DeploymentShowCommand(),
                new DeploymentLogsCommand()),
            new CommandGroup("repo", "Git repositories stacks deploy from.",
                new RepoListCommand(),
                new RepoAddCommand(),
                new RepoUpdateCommand(),
                new RepoRemoveCommand(),
                new CommandGroup("webhook", "The repository's push webhook.",
                    new RepoWebhookCreateCommand(),
                    new RepoWebhookRemoveCommand())),
            new CommandGroup("connection", "Git connections: credentials and environments for a git server's repositories.",
                new ConnectionListCommand(),
                new ConnectionAddCommand(),
                new ConnectionUpdateCommand(),
                new ConnectionRemoveCommand(),
                new ConnectionSetTokenCommand(),
                new ConnectionRemoveTokenCommand(),
                new CommandGroup("webhook", "The connection's organization webhook.",
                    new ConnectionWebhookCreateCommand(),
                    new ConnectionWebhookRemoveCommand())),
            new CommandGroup("user", "Users, their roles and API tokens (administrators).",
                new UserListCommand(),
                new UserShowCommand(),
                new UserCreateCommand(),
                new UserRenameCommand(),
                new UserActivationCommand(activate: false),
                new UserActivationCommand(activate: true),
                new UserSetAdminCommand(),
                new UserResetPasswordCommand(),
                new UserUnlockCommand(),
                new CommandGroup("role", "Roles on environments and stacks.",
                    new RoleGrantCommand(),
                    new RoleChangeCommand(),
                    new RoleRevokeCommand()),
                new CommandGroup("token", "The user's API tokens.",
                    new UserTokensCommand(),
                    new UserRevokeTokenCommand()),
                new CommandGroup("passkey", "The user's passkeys.",
                    new UserPasskeysCommand(),
                    new UserRemovePasskeyCommand())),
            new CommandGroup("notification", "Notifications the Discord bot posts (administrators).",
                new CommandGroup("channel", "Discord channels.",
                    new ChannelListCommand(),
                    new ChannelAddCommand(),
                    new ChannelUpdateCommand(),
                    new ChannelRemoveCommand()),
                new CommandGroup("rule", "Which events go to a channel.",
                    new RuleListCommand(),
                    new RuleAddCommand(),
                    new RuleUpdateCommand(),
                    new RuleRemoveCommand()),
                new NotificationHistoryCommand()),
        ];

        foreach (var node in tree)
        {
            root.Add(node.Build(host));
        }

        return root;
    }
}
