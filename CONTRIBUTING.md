# Contributing to OpenAudioLink

OpenAudioLink follows the approved Phase 1 architecture in
`docs/ARCHITECTURE.md`. Read it before proposing changes; do not redesign the
role model, the control-plane/audio-plane separation or the direct
Producer-to-Consumer audio path without prior discussion.

## Repository layout

```text
docs/       Phase 1 architecture, roadmap, hardware baseline, master prompt
protocol/   Protocol suite specifications (versioned, implementation-independent)
hub/        OpenAudioLink Hub (.NET solution: service, API, web UI shell, tests)
firmware/   ESP32 firmware (ESP-IDF projects and shared components)
```

## General rules

- Architecture before implementation. Interface changes start in `protocol/`.
- Keep receivers simple; hardware-specific code stays isolated behind profiles.
- Every protocol interface must be documented, versioned and testable.
- Prefer small, incremental, buildable commits.
- Do not add post-1.0 features prematurely.

## Coding conventions

### C# (Hub)

- Target: .NET 8 (LTS), nullable reference types enabled, warnings as errors.
- File-scoped namespaces, four-space indentation, `PascalCase` public members.
- Formatting is defined by `.editorconfig`; run `dotnet format` before committing.
- Tests use xUnit and live in `hub/tests/`. New behaviour needs a test.

### C (firmware)

- ESP-IDF style: `snake_case`, four-space indentation, no tabs.
- One component per concern; portable logic must not include SoC-specific
  headers. Hardware differences (C3 vs S3) are confined to hardware-profile
  code.
- Log with `ESP_LOGx` using a per-file `TAG`.

### Commits

- Imperative subject line, ≤ 72 characters, body explains why when not obvious.
- CI must pass (`hub` build + tests, firmware build) before merge.

## Building

### Hub

```bash
cd hub
dotnet build
dotnet test
dotnet run --project src/OpenAudioLink.Hub
```

### Firmware test node

```bash
cd firmware/testnode
idf.py set-target esp32s3
idf.py build flash monitor
```
