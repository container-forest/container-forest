# Comparison with similar tools

How container-forest relates to other self-hosted Docker managers, as of autumn 2026. The other tools move fast: check their own documentation before you decide. Corrections are welcome.

| | Portainer CE 2.x | Dockge 1.5 | Komodo 2.x | Arcane | **container-forest** |
|---|---|---|---|---|---|
| Development | frozen, fixes only | slow, active forks | active | active | active |
| Several servers | yes | yes (agents) | yes | yes (agents) | yes |
| Agent opens the connection | Edge mode only | — | yes | yes (Edge) | yes, the only mode |
| Deploy from git | yes | no | yes | partly | yes: public and private repos, on push, from CI |
| Secrets encrypted in git | no | no | no | no | yes: SOPS, an age key per server or stack |
| Roles per server and per stack | per server | no | yes | system-wide | yes |
| Manage from Discord | no | no | no | no | yes: commands and menus, same permissions |
| Discord notifications | no | no | webhook (text only) | — | through the bot, with *Restart* and *Logs* buttons under a crashed container |
| Separate restart / redeploy / redeploy with pull / down | no | partly | yes | partly | yes |

"—": we found no data. Arcane's entries come from its documentation and reviews, not our own testing.

## The tools

**Portainer** is the best-known and most complete tool. Portainer CE stays on 2.x and gets only security and bug fixes; 3.0 ships without a Community Edition (the free tier is Business Edition on three nodes) and puts Kubernetes first. It is heavy for small installations and awkward on a phone.

**Dockge** is a light, pleasant compose stack manager. Since 1.5 it manages several hosts through agents. It is built around a single user and does not deploy from git or handle secrets. The main repository moves slowly; new features appear in forks.

**Komodo** is the closest in architecture: outbound agents, key-based authentication, 2FA and OIDC. It is mature and does much more: image builds, Swarm, scripts, procedures. For most features container-forest doesn't compete with it.

**Arcane** is an open (BSD-3-Clause) panel in Go with outbound agents, free OIDC, MFA and passkeys, an image vulnerability scanner and a container console. Roles apply to the whole system rather than to a server or a stack.

## Where container-forest is different

1. **Secrets live in git, encrypted.** All configuration, secrets included, is versioned in one repository. Only the aggregator decrypts it, with a separate key per server (or per stack), and plain text never touches the aggregator's disk or the deployment log. With the `forest` git filter you edit secrets in plain text in your clone and commit them encrypted. The other tools store secrets in plain text or in their own database.
2. **Discord is a full interface.** The bot keeps a live connection to the aggregator rather than posting through a webhook, so messages carry buttons: *Restart* under a crashed container runs with the permissions of whoever pressed it, and *Logs* shows them the end of the log. `/dashboard` picks a stack and redeploys it, following the deployment in the same message.
3. **Access can be scoped to a single stack.** Someone can be allowed to restart one service on one server and nothing else. API tokens can have fewer rights than their owner: read-only, selected servers, an expiry date.
4. **One entry point.** The panel, the API, the agents and the bot all go through one domain on port 443 (Caddy with an automatic certificate). Agents need no inbound ports and the central host needs no extra tunnel port.
5. **Every deployment is tied to a commit**, recorded before any file is read, so even a failed deployment shows which version it was. Stacks can require signed commits.
6. **A deliberately narrower scope:** plain Docker and Compose, no image builds on servers, no Swarm or Kubernetes. Simpler to install and to reason about.

## Where it falls short

Maturity, the number of features, Swarm and Kubernetes, image builds, OIDC (not yet), a container console, and the size of the community.

## Sources

- Portainer 3.0 announcement: https://www.portainer.io/blog/the-docker-api-ceiling
- Portainer CE stays on 2.x: https://www.it-connect.tech/portainer-3-0-puts-kubernetes-first-as-ce-stops-at-2-x/
- Dockge: https://github.com/louislam/dockge
- Komodo releases: https://github.com/moghtech/komodo/releases
- Arcane: https://github.com/getarcaneapp/arcane
