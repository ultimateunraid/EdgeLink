# Additional static review
Reviewed selected lines of legacy Updater.cs, EdgeLinkApp.cs, installer and build workflow.
- Build workflow supports unsigned tagged releases when signing configuration is absent. Native release must fail closed on missing signing configuration; do not tag this branch.
- Legacy installer installs the pixel bridge and iCUE widget and offers Wowhead downloads (item IDs and zone names). Do not reuse its packaging unchanged.
- Legacy tray app exposes update actions through its widget and localhost service; native display manager must not instantiate this service or updater.
- Legacy updater consumes GitHub asset digests and filters prereleases. Full download/signature/redirect and local endpoint authorization audit remains outstanding.
- Native prototype has an unused SavedVariables declaration, no persistent positioning and no verified viewport support. Address before feature acceptance.
- Existing code remains reference only. No production files modified. This is a partial static review, not a full security audit.
