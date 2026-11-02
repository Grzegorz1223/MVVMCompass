# Changelog

## 1.2.0 (unreleased)

- Added opt-in eager tab initialization, detail navigation above tabs, and retained tab-switch compatibility callbacks, preserving existing tab and flyout defaults.
- Added public tab metadata, independent activity indicators, custom tab rows, body rails, and body-row overlays. Retained destination root views are available as read-only metadata and cleared when released.
- Added an optional body control template and detail margin for application-owned framing around retained tab and flyout content. Headers, footers and full-container overlays stay outside the frame; removing the template preserves retained state.
- Reduced tab activity updates to metadata changes without navigation layout reconciliation. Tab input availability remains separate from model navigation guards.
- Added view navigation callbacks that wait for native attachment. Covered compatibility tabs preserve undelivered activation until return and attachment, without repeating model activation or already delivered callbacks.
- Completed view and selection notifications for Back, Back-to-root, tab removal, reset, list replacement and failed-switch recovery, including outgoing cleanup failures. Parent selection updates precede view callbacks when restoring retained details.
- Added raw Toolkit popup and alert helpers, plus explicit-window APIs for covered containers and inactive-tab notifications.
- Balanced modal observers across actual dismissal, root replacement and window closure, keeping popup observation separate from view navigation. Cancelling a result wait does not dismiss visible UI, and pending requests cannot cross into a replacement root.
- Added `ModalPresentationBusyException` for overlapping window-level presentations; registered nested popups retain their existing behavior.
- Rejected raw view popup requests from navigation callbacks before UI dispatch, preventing a wait on the navigation operation's own lock.
- Added configure-before-open window APIs and a result with the created window and asynchronous closure/cleanup completion.
- Added `ViewModelBase.ConstructWithParent(parent, create)` for synchronous parent-aware construction without navigation entries, DI scopes or implicit lifetime ownership.
- Added runtime Type registration with constructor-preservation annotations; unowned navigator resolution now fails immediately with migration guidance.
- Added opt-in per-window fallback content after committed root installation/activation failure; preparation and cleanup-only failures preserve the appropriate live root.
- Unified registration, content navigation, popups, model cleanup and window teardown under one ownership path, removing the internal native-page navigation engine, locator and retained-container adapters while preserving public API compatibility.
- Renamed shared tab and container layout partials from Desktop to Layout to reflect their use on all supported platforms.
- Updated library dependencies to MAUI Controls 10.0.120, Community Toolkit 15.0.1 and MVVM Toolkit 8.4.2, matching the sample and DentalBee 3. Updated test dependencies to xUnit MTP v2 4.0.2 and Microsoft.Testing.Platform.MSBuild 2.5.2.
- Updated the sample to use public APIs, added shared-history, compact-panel and modal-depth examples, and moved native smoke fixtures to a separate friend assembly.
- Migrated cleanup tests to the current API, retired coverage for removed internal contracts, and added regression coverage for framing, tab lifecycle and popup dispatch.
- Updated the desktop migration guide and removed workload-set pins from the repository and sample-bundle SDK files. The Windows helper records the installed toolchain without installing workloads.

## 1.1.0

- Added asynchronous initial-root selection, customizable startup and failure content, and coordinated application-owned root transitions.
- Improved toolbar stability, inherited appearance and geometry, icon sizing, and compact layouts.
- Improved flyout selection, input and focus handling, retained navigation, and guarded dismissal.
- Kept runtime loading under application control, with typed templates, inherited backdrops, and overlapping loading scopes. Navigation does not start a loader.
- Added deferred typed popup requests with origin validation, optional deduplication, cancellation outcomes, and reliable cleanup.
- Extended shared navigation and lifecycle fixes to tabs, and added tab-bar padding, item spacing, and scrollbar configuration for all four edges.

## 1.0.0 Initial Release
