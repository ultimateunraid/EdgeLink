# Xeneon Edge display contract
Target: Corsair Xeneon Edge, 2560 x 720 landscape, below primary monitor by default.
Offhand owns game window spanning and 3D viewport. EdgeLink owns its dashboard root, child frames, scaling, touch targets and layout. Do not independently resize WorldFrame while Offhand is active.
Use a named native root frame (EdgeLinkNativeDashboard) for placement. Initial integration is manual positioning through supported Offhand UI; do not assume undocumented API names. Validate actual Offhand release and API before adding automatic integration.
Use a 2560 x 720 logical design canvas with uniform scaling to the allocated UI rectangle; convert physical pixels to UI units using measured window/UI dimensions. Never assume primary monitor resolution or Windows DPI.
Test centered and offset Edge arrangements, mixed DPI, focus, touch coordinate alignment and game viewport aspect ratio. No synthetic input or screen capture.
If Offhand is absent or placement is not configured, show only an explicitly opened in-game preview; do not modify the game viewport.
Development only; no production packaging until acceptance tests and owner approval.
