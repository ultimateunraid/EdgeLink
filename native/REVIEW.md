# Initial migration review
Scope: README, tree and core source inspection; not a completed line-by-line audit.
- EdgeLink.lua: retire SplitPayload, EncodePage, PlaceStrip, CreateStrip, ShowPage and command bindings. Refactor collectors into typed internal state. Audit protected/secret values, combat behavior, quest area sampling and third-party calls.
- EdgeLinkCore.cs: retire Decode, CopyFromScreen, SendCommand, keybd_event, live HTTP bridge. Port CharStore and TrailStore into account SavedVariables only after API review.
- widget/index.html: port renderBags, renderGear, renderQuests, renderProf, renderSession, renderMap and route logic into native frames. No external polling or commands.
- EdgeLinkApp.cs and Updater.cs: review installer/update security and retain only optional installation/window management.
- Review Wowhead asset rights; prefer client-provided textures.
- Review all installer, CI, tests and privacy documentation before release. Existing build still packages legacy code.
- Validate actual Forever client API, viewport geometry, touch input and performance before feature parity claims.
- Never derive restricted values or automate gameplay; unavailable data must remain unavailable.
