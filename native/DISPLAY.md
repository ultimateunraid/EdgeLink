# Xeneon Edge display contract
Target: 2560 x 720 landscape, below primary by default.
Default: original EdgeLink Windows display manager owns OS window placement; native Lua addon owns dashboard and gameplay viewport only through validated supported APIs. Offhand compatibility and manual modes are optional; never allow simultaneous viewport owners.
Use a 2560 x 720 logical canvas and measured physical-to-UI conversion. Handle negative coordinates, offsets and mixed DPI. Explicit user selection and reversible changes only.
Until viewport support is validated, do not alter WorldFrame; keep preview mode. No Offhand code copied. No screen capture, synthetic input, memory access or external gameplay telemetry.
Development only; no production packaging, release or merge until acceptance tests and owner approval.
