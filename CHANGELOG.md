# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [5.0.0] — 2026-10-22

First public release as **Code Obfuscator**.

### Added
- Per-project JSON profile (`ProjectSettings/CodeObfuscator.profile.json`): every category toggle, safety limit,
  brand pair and custom rule. Export / Import so a tuned setup seeds the next project. Presets Standard /
  Conservative / Aggressive.
- Headless entry point `CodeObfuscator.ObfBatch.Run` (`-obfAction scan|apply|verify|restore`) for CI.
- Pre-build gate (`IPreprocessBuildWithReport`) that refuses a player build until the project is obfuscated.
- Auto-detection of SDK / third-party folders (never touched); extensible from the profile.
- Incremental journal + one-click **Restore last backup** that survives an editor crash.
- Own-code markers are now profile-driven (`reconstructionMarkers`); nothing project-specific is built in.

### Notes
- This is the first release extracted into a standalone, reusable package. Earlier internal revisions
  (the staged pipeline, the runtime-string safety rules, the brand pass) are folded into 5.0.0.
