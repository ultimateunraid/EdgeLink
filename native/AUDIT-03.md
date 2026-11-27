# Static audit findings (continued)
Scope: targeted source inspection, not a completed line-by-line audit or penetration test.
- Critical release gate: Updater.cs 278–290 allows missing SHA256; 312–320 accepts any installer when running executable has no trusted publisher. Combined with unsigned release workflow, this is a fail-open update path. Native must not reuse it; require digest and trusted expected signer, or omit updater.
- High privacy: EdgeLinkCore.cs 1157–1179 exposes state, characters, trails and asset requests without command authorization; 1199–1201 sends wildcard CORS and private-network permission. Browser enforcement varies; do not assume localhost is private. Native has no HTTP listener.
- High authorization: EdgeLinkCore.cs 900–907 accepts absent/null/non-HTTP origins; 1140–1155 uses presence of a custom header, not authentication, and processes mutation routes before method validation. Remove endpoints in native.
- High policy: EdgeLink.lua 1487–1494 and 1562–1580 continue emitting encoded pages during combat even when snapshot rebuild is paused. Position handling is separate; do not describe transport as stopped during combat. Remove entire transport.
- Medium trust: Install-EdgeLinkCert.ps1 installs a local root CA. Native must not install a root CA.
- Medium privacy: external map/icon requests disclose identifiers and zone queries; native uses client assets only.
- Widget uses innerHTML extensively; many values are escaped, but full sink-by-sink validation remains outstanding. Native avoids HTML.
- Legacy tests validate encoding and input emulation; these are not native acceptance tests. Reuse only independent fixtures after review.
- Remaining: full line-by-line review of collectors, widget rendering, build scripts and native code, dependency/license review, runtime testing. No claim of Blizzard approval.
