# ADR-0004. The stack directory isn't replaced: bundle files are updated in place using a manifest

- Status: accepted
- Date: 2026-09-30

## Context

The original scheme for writing a stack's files on the agent: the bundle is written to `<stack>.tmp`, the current directory is renamed to `<stack>.prev`, and `.tmp` to `<stack>`. It's atomic for the whole bundle, but breaks a common case, container data inside the stack directory (`volumes: - ./data:/var/lib/postgresql/data`):

1. On a redeploy, `./data` moves to `.prev` along with the directory, and the new directory doesn't have it: it isn't in git.
2. The path in the container's configuration hasn't changed, so `compose up` **doesn't recreate** the container, and it keeps writing to the old inode, i.e. into `.prev`.
3. The next deployment deletes `.prev` along with the data.

An unchanged container would also see file bind mounts (`./nginx.conf:/etc/nginx/nginx.conf`) in their old version.

## Decision

1. The stack directory `<StacksDirectory>/<stack>` is created once and never moved again. The agent changes only the bundle's files in it: each file is prepared in `<stack>.tmp` and renamed over the target (atomic per file). Everything else in the directory belongs to containers and isn't touched.
2. The list of the last bundle's files is the manifest `<StacksDirectory>/<stack>.manifest.json`. Files from the previous manifest that aren't in the new bundle are deleted, and directories left empty are removed. The manifest lives **outside** the stack directory, because a container mounting `.` could rewrite it and make the agent delete someone else's files. An unreadable manifest is ignored: extra files stay, but nothing is deleted at random.
3. Before writing, the agent checks there are no symbolic links between the stack directory and each file, and no files where directories should be. A container can create a link to `/` in a mounted directory, and the agent runs as root. Where the bundle has a file and the host has a directory, the deployment is rejected (`ERROR_CODE_COMMAND_FAILED`) before any change. Paths from the manifest are checked the same way before copying and deleting.
4. The previous bundle (regular files only) is copied to `<stack>.prev` for rollback; secret content doesn't go there.
5. Secrets in `<SecretsDirectory>/<stack>` on tmpfs are replaced as a whole directory (`.tmp` → current → `.prev`), as before: containers don't write there. The stack directory holds a symbolic link to each secret at its path.
6. The order: checks → preparing `.tmp` → the copy in `.prev` → a manifest merging old and new files → replacing secrets → renaming files → deleting stale ones → the final manifest.

## Consequences

- The bundle is no longer replaced atomically as a whole: if the agent crashes midway through the renames, the directory holds some of the new files. The intermediate merged manifest guarantees the next deployment cleans up everything extra.
- The link check has a window between checking and writing (TOCTOU). To use it, a container would have to swap a directory exactly during a deployment; only writing through `openat` with `O_NOFOLLOW`, which .NET doesn't offer directly, would close it completely.
- Checked live on `example-voting-app`: after a redeploy, the directory's inode, the containers and the files in `data/` stayed the same.
