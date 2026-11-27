# Development-only acceptance gates
No merge, production release, installer publication or update-channel change without owner approval.
## Client smoke test
- Disable legacy EdgeLink and exit companion. Install only native/EdgeLinkNative.
- Confirm addon loads in Forever; enable script errors and run /edgenative.
- Verify tabs, dragging, hide/show, FPS, gold and XP against native UI.
- Enter and exit combat; verify no restricted-value errors and no protected actions.
## Display acceptance
- Record main monitor resolution, scaling, Edge resolution and relative placement.
- Test native frame in manually spanned window before introducing window-management software.
- Verify primary viewport aspect ratio, cursor alignment, touch clicks, focus, alt-tab and fullscreen transitions.
## Feature acceptance
- Map: native map art, position and pins only when exposed.
- Quests: objective display and explicit native tracking actions.
- Bags/character/professions/session: compare with built-in UI; test unavailable values.
- Integrations: test separately with and without each optional addon.
- Confirm no screen capture, external telemetry, synthetic input or protected API bypass in native distribution.
## Release gate
Complete source audit, tests, asset rights review, packaging isolation, policy review and owner approval. Blizzard approval has not been obtained. Legacy branch contents are not a native distribution.
