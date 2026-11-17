# Shared folder

The shared folder is a per-machine directory at `C:\zzz` that is synchronized through the hub. It is
for small files and folders that should exist on every managed Windows computer, such as scripts,
configuration snippets, licence files or installers. It is not a backup system, a distributed file
system or a replacement for a company file server.

## How it works

- The hub owns the authoritative metadata and a content-addressed blob store under its data folder.
- The hub-local `C:\zzz` participates directly, even when no agent is installed on the hub computer.
- Every online agent reports a manifest of its `C:\zzz` through the pinned mutual-TLS management API.
- The hub checks once every 15 seconds by default, pulls new or changed files into its blob store, and
  pushes central changes to the other peers.
- A file placed in `C:\zzz` on any machine is therefore copied to the hub and then to the other online
  machines. Offline machines reconcile when they come back online.
- The hub machine's own agent is excluded from the remote agent loop when its machine name matches the
  hub. The hub-local folder is handled directly so the same directory is not managed twice.

`C:\zzz` is a **dedicated shared-folder root**. It is not treated as an ordinary permitted
file-transfer root. The agent runtime adds the configured shared-folder path as a dedicated root only
for the shared-folder APIs; the default `AgentAllowedRoots` setting stays `C:\Users;C:\ProgramData`.
The path can be changed in Settings (`sharedFolder.path`), but agents and the hub must use the same
path.

## Using it

1. Open **File Transfer** and select the **Shared folder** tab.
2. Use **Add files** or **Add folder** to copy items into the hub's `C:\zzz`. The console never
   overwrites an existing name; it creates `name (2).ext` (or a numbered folder) instead.
3. Alternatively, open `C:\zzz` in File Explorer on any managed computer and place files there
   directly. **Open folder** opens the hub's copy.
4. **Refresh** updates the status cards and central file list. Synchronization itself runs in the
   background.
5. The **Hub copy** table is the central view. **Agents** shows remote peers that have reported a
   manifest and whether they are online (heartbeat within five minutes).

Files are limited to 256 MB each and 20,000 files by default. Both limits are settings
(`sharedFolder.maxFileBytes`, `sharedFolder.maxFiles`). Oversized or temporarily unreadable files are
reported as skipped and are not treated as deletions.

## Conflicts and deletions

- **New file:** uploaded to the hub and pushed to every other peer.
- **Changed file:** if the peer's previous copy matches the central copy, the edit is accepted. If
  the central copy also changed since that peer last observed it, the central copy wins and the
  peer's version is renamed:
  `name (conflict from MACHINE yyyyMMdd-HHmmss).ext`.
- **Conflict copies are ordinary files.** They are uploaded on the next scan, so no bytes are
  discarded.
- **Delete:** the hub records a tombstone. A deletion wins only if the central file has not changed
  since that peer last observed it. If the central copy changed, the central file is restored to the
  peer. If a peer has a new or edited local file at a path the hub has deleted, the local file is
  preserved as a conflict copy.
- **Offline agents:** changes remain in the central store and reconcile when the agent reconnects.
- **Restart:** state is persisted in SQLite (`SharedFolderFiles`, `SharedFolderAgentFiles`,
  `SharedFolderLocalFiles`, `SharedFolderSyncState`), so a hub restart does not re-upload unchanged
  files. Blobs are addressed by SHA-256 and reused.

## Safety properties

- Relative paths are validated with `PathGuard.TryResolveRelativeWithinRoot`: traversal, rooted
  paths, alternate data streams, Windows reserved device names, trailing dots/spaces and the internal
  staging directory are rejected.
- The scanner skips reparse points and never follows junctions or symbolic links.
- The hub writes agent files through a temporary file under `C:\zzz\.rdh-sync` and finalizes the
  destination only after SHA-256 verification.
- Incoming files are limited by size and count. A peer that exceeds the count or has unreadable
  files is not silently treated as having deleted them.
- All hubs and agents use the existing mutual-TLS and certificate-pinning model. The manifest is a
  read-only agent endpoint; file reads and writes use the existing authenticated file API.
- Successful peer cycles, conflict copies and deletions are recorded in the audit chain under
  `File.SharedFolderSynced`, `File.SharedFolderConflict` and `File.SharedFolderDeleted`.

## Limitations

- This is LAN-oriented pull/poll synchronization, not a real-time distributed lock. Two machines can
  edit the same file between polls; the conflict copy preserves the losing version, but it is not an
  automatic merge.
- `C:\zzz` is a local path. On a computer where the agent runs as `LocalSystem`, files written by the
  agent are owned by `SYSTEM`; local users need write access to the folder if they are expected to
  drop files there. The hub applies a modify ACL for `BUILTIN\Users` when it creates the folder, but
  an existing folder keeps its ACL.
- The folder is not encrypted by RemoteDesk Hub. Use BitLocker or an encrypted volume if the files
  are sensitive.
- Multi-machine and physical-LAN behavior has not been tested locally because only one Windows
  computer was available. The implementation is covered by real SQLite tests for the hub-local peer
  and a simulated agent transport, and by a local end-to-end check of `C:\zzz` import and UI status.
