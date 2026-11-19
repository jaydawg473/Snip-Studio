# Viewport geometry checks

This standalone console project links the actual WinUI preview's `ViewportState.cs`; it does not load WinUI, WinForms or a display surface. Its checks use known image-to-DIP examples and behavioral invariants: physical 100%, visible viewport centering/padding, large-image Fit, pointer-anchored zoom, manual resize/DPI anchors, bounded pan, image-edge clipping and invalid-input state preservation.

The 69/69 and 49/49 gap examples feed two explicit usable viewport sizes into the model. They do not establish that a real WinUI toolbar or window supplies those bounds. Native layout, pointer capture/cancellation, rendered images, physical DPI/monitors, keyboard focus and accessibility require separate acceptance checks.

Source was authored without compilation or execution. Build with the pinned, reviewed SDK. Sign and verify the check executable/assembly and its runtime dependencies before execution, then pass `--output` with a new, empty evidence directory. The check writes `viewport-checks.json` and never replaces previous evidence.
