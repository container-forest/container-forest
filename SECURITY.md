# Security policy

container-forest holds the keys to your servers: the aggregator decrypts your secrets and its agents run as root. We take reports seriously and appreciate them.

## Reporting a vulnerability

**Please don't open a public issue.** Report it privately through GitHub: the repository's *Security* tab → *Report a vulnerability*. Include what an attacker can do, the steps to reproduce, and the version (image tag or `forest --version`).

We'll acknowledge the report within a few days, keep you posted while we fix it, and credit you in the release notes unless you'd rather stay anonymous. Please give us reasonable time to release a fix before disclosing it.

## Supported versions

Until 1.0, only the latest release gets security fixes. Upgrade notes are in [docs/installation.md](docs/installation.md#6-upgrades).

## Scope

Anything that breaks one of the [security invariants](docs/security-invariants.md) is a vulnerability, for example: an agent receiving private keys, permissions bypassed by talking to the API directly, decrypted secrets ending up in logs, or a command reaching an agent that isn't one of its typed commands.

Things that work as documented aren't, such as an Editor without a compose policy having root on the host, or applications printing their own secrets to logs. If the documentation doesn't make a risk clear, that's worth an issue too.
