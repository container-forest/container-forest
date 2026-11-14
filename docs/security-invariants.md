Rules no PR may break:

1. Private age keys exist in readable form **only on the aggregator**. Agents never receive them. The aggregator's backup takes them out only encrypted to an offline key the aggregator doesn't have (ADR-0014).
2. The agent **opens no inbound ports**. The agent initiates every connection.
3. An agent's `environmentId` comes from its authentication, **never from the content of a message**.
4. Permissions are checked **only on the aggregator**. The panel and the bot only hide buttons you can't use.
5. The agent runs only a **fixed set of typed commands**. Never "run an arbitrary shell command".
6. Decrypted secrets are **never logged**. Logs and the audit log mask values.
7. Commands to an offline agent are **not queued**: they fail at once.
8. Agent credentials, user passwords and API tokens are stored only as hashes.
9. The Discord bot's token is protected like an administrator's: compromising the bot lets you act as any linked user.
