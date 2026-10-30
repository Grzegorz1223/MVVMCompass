# Changelog

## 1.1.1-dev.7 (local preview)

- Renamed the shared tab and container layout partials from Desktop to Layout; these controls apply on all supported platforms.
- Removed the internal native-page navigation engine, its locator and retained-container adapters. Registration, content navigation, popups and window teardown now use one ownership path.
- Extracted shared model cleanup and native modal observation, preserving public API compatibility and the optional tab-switch lifecycle.
- Migrated relevant cleanup tests to the current API and retired tests and smoke scenarios for removed internal contracts.

## 1.1.1-dev.5 (local preview)

- Added explicit-window popup and alert APIs for covered containers and inactive-tab notifications, using the existing host and popup ownership machinery.
- Added `ModalPresentationBusyException` for overlapping window-level presentations; registered nested popups retain their existing behavior.
- Balanced observers across actual dismissal, root replacement and window closure; cancellation of a result wait does not dismiss visible UI. Pending requests cannot cross into a replacement root.
- Added `ViewModelBase.ConstructWithParent(parent, create)` for synchronous parent-aware construction without navigation entries, DI scopes or implicit lifetime ownership.
- Corrected the external desktop migration guide and removed workload-set pins from both sample-bundle SDK files; the Windows helper records the installed toolchain without installing workloads.

## 1.1.1-dev.4 (local preview)

- Exposed retained destination root views as read-only metadata, clearing references when released.
- Added native-attachment-deferred view navigation callbacks, preserving tab-switch compatibility and keeping popup observation separate.
- Added public raw Toolkit popup/alert helpers and balanced modal observation tied to actual popup lifetime, including cancellation of a result wait.
- Added configure-before-open window APIs and a result with the created window and asynchronous closure/cleanup completion.
- Added runtime Type registration with constructor-preservation annotations; unowned navigator resolution now fails immediately with migration guidance.
- Added opt-in per-window fallback content after committed root installation/activation failure; preparation and cleanup-only failures preserve the appropriate live root.
- Made the ordinary sample public-API-only; native smoke fixtures build as a separate friend assembly. Added a generic compact-panel and modal-depth demonstration.

## 1.1.1-dev.3 (local preview)

- Updated the library's minimum dependencies to MAUI Controls 10.0.120 and Community Toolkit 15.0.1, matching the sample and DentalBee 3. MVVM Toolkit 8.4.2 remains current.
- Updated test dependencies to xUnit MTP v2 4.0.2 and Microsoft.Testing.Platform.MSBuild 2.5.2, the latest stable versions checked on 30 October 2026.

## 1.1.1-dev.2 (local preview)

- Added an optional body control template and detail margin for application-owned framing around retained tab and flyout content.
- Kept header/footer and full-container overlays outside the frame; removing the template restores the original body and retained state.
- Added frame-transition regression coverage and clarified that tab input availability does not replace model navigation guards.

## 1.1.1-dev.1 (local preview)

- Added opt-in eager tab initialization, detail navigation above tabs, and retained tab-switch compatibility callbacks.
- Added public tab metadata and independent activity indicators, custom tab rows, body rails, and body-row overlays.
- Reduced tab activity updates to metadata changes without navigation layout reconciliation.
- Lowered minimum MAUI Controls and Community Toolkit dependencies to 10.0.90 and 14.2.2; existing tab and flyout defaults remain unchanged.
- Added shared-history sample navigation and regression coverage for both behaviors.

## 1.1.0

- Added asynchronous initial-root selection, customizable startup and failure content, and coordinated application-owned root transitions.
- Improved toolbar stability, inherited appearance and geometry, icon sizing, and compact layouts.
- Improved flyout selection, input and focus handling, retained navigation, and guarded dismissal.
- Kept runtime loading under application control, with typed templates, inherited backdrops, and overlapping loading scopes. Navigation does not start a loader.
- Added deferred typed popup requests with origin validation, optional deduplication, cancellation outcomes, and reliable cleanup.
- Extended shared navigation and lifecycle fixes to tabs, and added tab-bar padding, item spacing, and scrollbar configuration for all four edges.

## 1.0.0 Initial Release
