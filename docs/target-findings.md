# Installed target findings

**Date:** 27 September 2026  
**Method:** Read-only inspection of this Windows 11 x64 installation via `Get-AppxPackage`, package manifests, `Get-StartApps` and running process metadata. No installed target was closed or restarted.

Two distinct packaged applications are present:

| Start menu name | Package family name | Activation ID | Manifest executable | Observed version |
|---|---|---|---|---|
| ChatGPT | `OpenAI.Codex_2p2nqsd0c76g0` | `OpenAI.Codex_2p2nqsd0c76g0!App` | `app/ChatGPT.exe` | `26.924.2738.0` |
| ChatGPT Classic | `OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0` | `OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT` | `app/ChatGPT Classic.exe` | `1.2026.190.0` |

The versioned `WindowsApps` paths are installation details, not durable launch identities. Several processes for each application are running. A PID or executable name alone cannot identify one logical instance reliably, and launcher handoff cannot be inferred from this static inspection.

The user has been asked which of these to protect first. Pending that choice, development uses the controllable `Relight.TestTarget` and neither installed app is manipulated. M0's actual activation/discovery spike and job-continuation check remain open. Product requirements continue to distinguish application relaunch from resumed work.
