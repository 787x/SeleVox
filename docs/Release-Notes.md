# SeleVox {{TAG}}

Windows 11 x64 portable distribution. Built from tag `{{TAG}}`, commit `{{COMMIT}}`.

SeleVox provides selection reading and global hotkeys, region OCR with Chinese/English models, configurable LLM questions and editable answers, local/online TTS, pause/resume/stop and multi-device WASAPI playback. The audio workbench generates and exports WAV, supports MiMo voice design/cloning and optional AI assistance. CLI and local MCP stdio reuse the desktop application and Agent presets. Optional virtual microphone routing supports an independently installed VB-CABLE.

This release includes automated self-contained portable packaging, extracted-file integrity checks and SHA-256 checksums. See the tag's commits and merged PRs for the version's changes.

Download `SeleVox-{{TAG}}-win-x64-portable.zip` and `SHA256SUMS.txt`, verify the ZIP with `Get-FileHash -Algorithm SHA256`, extract the entire archive and start `SeleVox/SeleVox.App.exe`. No separate .NET runtime installation is required. [CLI / MCP documentation](https://github.com/787x/SeleVox/blob/{{TAG}}/docs/CLI-MCP.md) describes `SeleVox.Cli.exe` and MCP client configuration.

Online providers require your own configuration and API keys and may incur charges. Credentials remain in Windows Credential Manager; settings and samples remain in the current user's LocalAppData. Portable means no installer, not that all user data lives beside the executable.

VB-CABLE is not bundled: install a supported driver separately and validate routing before use. This build has no code signing certificate; Windows may display an unknown-publisher warning. SHA-256 checks file integrity and is not a digital signature or publisher authentication. UI Automation support varies by application; password controls are excluded. Real desktop OCR, audio devices, virtual capture, online providers and MCP desktop connections require user acceptance testing. Edge is an experimental online provider whose availability can change.
