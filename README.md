# LightDrop

> Zero-config local sharing between your own devices.

LightDrop is a lightweight developer companion that lets you instantly transfer files, screenshots and clipboard content between your computers over your local network.

No cloud.

No accounts.

No installation.

Just run the binary and your devices find each other.

## Why?

Moving a screenshot from your desktop to your work laptop shouldn't require:

- email
- OneDrive
- Slack
- USB drives
- SCP
- shared folders

LightDrop makes nearby devices feel like they're part of the same workstation.

## Status

**Early development.** The daemon runs and reports its identity; nothing is transferred yet.
See `docs/Roadmap.md` for what lands when.

```bash
lightdrop daemon   # run the daemon
lightdrop health   # ask it who it is
```

## Goals

- Automatic device discovery
- Local network only
- Encrypted peer-to-peer connections
- Zero configuration
- Cross-platform (Windows & macOS)
- Single portable executable

## Planned

- Peer discovery
- Secure pairing
- File transfer
- Clipboard text
- Clipboard images
- Screenshots
- Explorer / Finder integration
- Claude Code image handoff
- Notifications
- Remote actions

## Philosophy

LightDrop is **not** another cloud storage service.

It is not Dropbox.

It is not Syncthing.

It is not a network drive.

It simply makes moving things between your own nearby computers effortless.

## Example

```bash
lightdrop peers

Desktop
MacBook Air
Work Laptop

lightdrop send screenshot.png "Work Laptop"
```

The file immediately appears on the destination machine.

## Design Principles

- Zero configuration
- Local-first
- Privacy by default
- Portable
- Small
- Developer-friendly

## Documentation

- Product Requirements Document: `docs/PRD.md`
- Architecture: `docs/Architecture.md`
- Protocol: `docs/Protocol.md`
- Roadmap: `docs/Roadmap.md`
- Decisions: `docs/DECISIONS.md`

## License

MIT