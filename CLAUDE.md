# CleanPrint — Project Context for Claude Code

Windows virtual-printer app (WPF, .NET). Captures print jobs via an XPS-based
virtual printer, lets the user whiten/crop/annotate/split scanned pages, then
prints. Built iteratively across many sessions with claude.ai — this file is
the persistent context handoff. **Run `git log` first** — commit messages in
this repo document the actual reasoning (root causes found, approaches tried
and rejected, why) for nearly every fix below, in far more detail than fits
here.

## Architecture
- `CleanPrint.Core` — shared models, paths, IPC, logging.
- `CleanPrint.Watcher` — headless background process, watches capture folder,
  converts XPS → PNG, notifies the App. Runs without admin.
- `CleanPrint.App` — the WPF UI. `MainViewModel.cs` and `MainWindow.xaml`/
  `.xaml.cs` hold almost all the logic; `PolygonViewModel.cs` is the
  per-polygon state (vertices, purpose, color/thickness, selection).
- `Scripts/` — `Publish.ps1`, `AddCleanPrinter.ps1`, `RemoveCleanPrinter.ps1`
  (PowerShell, not part of the .NET build).

## Versioning convention
Each meaningful checkpoint gets an annotated git tag `ver_0.1.N`, created
**only once the person has tested and confirmed it works**. If a change
regresses something, the fix is to `git checkout ver_0.1.N -- .` back to the
last good tag rather than trying to patch forward blind. Several regressions
in this project's history happened from stacking multiple risky changes
before testing — the working pattern that recovered from them was: one
isolated change, deliver, wait for confirmation, then proceed.

## The single most fragile area: Freeze Row/Column's pinned overlay
`MainWindow.xaml`'s Freeze Row/Column pinned overlay (the part that keeps a
frozen band fixed on screen while the rest scrolls) has broken completely
**twice** from changes that looked safe on paper — once from a dynamic
`ScrollViewer.ViewportWidth` binding resolving to 0 and collapsing the whole
overlay, once from a seemingly-independent margin/wrapper change. Both times
the fix was reverting to the last known-good tag rather than patching further.
Known accepted limitation, deliberately not touched again: the frozen band
overlaps the scrollbar (right for Freeze Row, would be bottom for Freeze
Column) — a fixed-margin attempt at fixing this is what caused one of the two
regressions. Treat any change to this specific overlay/positioning code as
high-risk; test in isolation before layering anything else on top.

## Key coordinate-system facts (get these wrong and things silently misalign)
- Polygon vertices are always stored in **original, un-hidden image-space**
  DIU (device-independent units, matching `PreviewImage.Width/Height` before
  any Hide Row/Column is applied) — never in zoom-scaled or post-hide
  "display" coordinates.
- `MapToDisplay`/`MapToOriginal` in `MainViewModel.cs` convert between that
  original space and the current on-screen (zoom-scaled, gap-closed-if-hiding)
  space. Every click goes through `GetImageSpacePosition` →
  `MapDisplayToOriginal` before touching a polygon's `Vertices`.
- Zoom drift and click-misalignment bugs in this project were reliably caused
  by manually replicating WPF's padding/centering/transform math by hand.
  The fix that actually worked was always to ask WPF directly via
  `TransformToVisual` rather than recompute the geometry ourselves.

## Known, accepted limitations (not bugs — don't "fix" without discussion)
- Split/Print still use the original page — Hide Row/Column only affects the
  live preview, not split tiles or printed output.
- Freeze Row/Column's pinned overlay renders over the scrollbar.
- Freeze Row/Column no longer requires/enforces spanning the full page
  width/height (this was removed deliberately) — a narrower band pins/hides
  only its own drawn extent.

## Testing
No automated tests. Verification has been manual, in Visual Studio, one
change at a time. There is no CI — `dotnet build` locally is the only signal
before the person actually runs the app.

## The logo (ver_0.14.0)
- Source of truth: `CleanPrint.App/Assets/CleanPrint-logo.svg` (the "Before / After" page, Rose colourway). Don't edit the .ico or the PNGs by hand.
- `python Scripts/make_icon.py` (needs `pip install cairosvg pillow`) regenerates `CleanPrint.ico` (16-256 px, each size rendered at exactly that
  size), `logo-64.png` (title bar) and `logo-256.png` (About dialog). Re-run it whenever the SVG changes, and commit the outputs.
- Used as: `<ApplicationIcon>` of both CleanPrint.App and CleanPrint.Watcher (the Watcher refers to the App's copy), the `Icon` of every window
  (XAML `pack://application:,,,/Assets/CleanPrint.ico`; the floating Effects window sets it in code), the title bar logo, and the About dialog.
  The three files are `<Resource>` items in CleanPrint.App.csproj — a new asset referenced by a pack:// URI must be added there too.
- Other colourways (Slate, Amber, Teal, Violet, Ocean, Emerald, Sunset) were drawn and not chosen; only Rose is in the repo.

## Icons
- Toolbar/title-bar/status-bar icons are filled paths from Material Design Icons (Apache-2.0), in `CleanPrint.App/Themes/Icons.xaml`, each normalised to
  explicit WPF path syntax with a leading `F1` (non-zero fill; SVG's default, where WPF's is even-odd). Credit: `THIRD-PARTY-NOTICES.md`.
- A `<Path Style="{StaticResource IconPath}">` stretches the geometry's own TIGHT bounding box to its Width/Height (Stretch=Uniform), not the 24x24 grid:
  a narrow icon (a chevron) fills its box in height and looks larger than a squarer one. Size icons by how big they actually come out.
- The one icon NOT straight from the pack is **IconSave**: the pack's `floppy` with a plus badge cut into the corner, built by geometry in
  `Scripts/make_save_icon.py` (`pip install svgpathtools shapely`), which prints exactly the data in Icons.xaml. Delete is the pack's `trash-can-outline`.

## Implicit styles: define each ONCE (ver_0.19.1 — a bug that hid for four versions)
- The slim scrollbar of 0.15 never applied, because an older `<Style TargetType="ScrollBar">` still sat directly in `App.xaml`'s `Application.Resources`, and a style declared directly there beats
  the same key in a merged dictionary (Controls.xaml). The old one was also ineffective on its own (it set Width=10 but the stock template's MinWidth=17 won). It is removed; `App.xaml` now says why.
- Before adding an implicit style (`<Style TargetType="X">` with no x:Key), grep every .xaml for another one with the same TargetType. A scan that lists them all is a few lines of Python.
- Lists (ver_0.19.1): `Themes/Controls.xaml` has NAMED styles `AppListView` / `AppListViewItem` / `AppListHeader` / `AppListBox` / `AppListBoxItem` in the CMS look (colours sampled from its screenshot: header #F1EEFA,
  selected #D3C4F5, border #DCD7E9, separators #EBE8F3, header text #3B4A5E). Fixed colours, not theme brushes, because the windows with lists aren't themed. Applied to the Open Job list and the preset-name list.
- Never set MaxWidth/MaxHeight on a window to the work area: a maximized WPF window is ~8 units larger than the work area on each side, so the cap leaves black strips when maximized.

## Scrollbars (ver_0.15.0; actually effective from ver_0.19.1)
- `Themes/Controls.xaml` restyles EVERY scrollbar app-wide with implicit styles: 8 px, no arrow buttons, rounded thumb on a transparent track, darker on
  hover/drag (`AppScrollThumb*Brush` in both themes). It also replaces the ScrollViewer template — identical structure and part names to the stock one
  (`PART_ScrollContentPresenter`, `PART_VerticalScrollBar`, `PART_HorizontalScrollBar`; the control finds them by name, don't rename) except that the corner
  square is transparent. Nothing in the code assumes a scrollbar size.

## Mirroring (ver_0.15.0)
- `MainViewModel.MirrorSelection`: flips the selection about the middle of the selection AS A GROUP. Text stays readable: its box moves to the mirrored place and its
  rotation is reflected (`TextGeometry.MirrorTextBox`, tested against brute-force corner geometry). Ellipses keep their radii (centre moves). Freeze/Hide bands are
  skipped — flipping one would silently change which rows/columns are pinned or hidden.

## Blank jobs (ver_0.16.0)
- Title-bar "New Blank Job" -> `NewBlankJobDialog` (paper size A3/A4/A5/A6/B5/Letter/Legal/Tabloid/Custom, orientation, DPI, number of pages; live pixel size + validation, all
  arithmetic in Core `BlankPageSizes`, tested; ceiling 50 megapixels a page) -> `BlankJobService.CreateJob` writes `Jobs/Blank-<paper>-<timestamp>/page_NNNN.png` (white, 8-bit
  grey), `capture-info.json` (DPI), `blank-job.json` (`BlankJobInfo`: paper, orientation, pixel size, DPI — its presence is what makes a job "blank") and a `job-state.json` with
  every effect OFF (auto-crop on an empty page could shrink it) -> `LoadJob`.
- A blank job (and only a blank job, `MainViewModel.IsBlankJob`) gets the page menu beside the status-bar page navigation: add at end / insert after this page / delete this page.
  Adding or deleting renames the page files AND re-keys everything keyed by page number — annotations (`JobAnnotationSettings.Pages`), split overrides (`_pageOverrides`, by index),
  undo history (`_histories`, key `jobId|page|variant`) — using `PageRenumbering` (Core), whose rename plans are collision-safe and tested by simulation. Anything new that is keyed
  by page number or index MUST be added to `MainViewModel.ReKeyPagesAsync`.
- "Include in Print" stays opt-in on blank jobs (the user's decision); the dialog says so.

## Pictures on a page (ver_0.17.0)
- `ShapeKind.Image`: a picture placed on a page of ANY job (blank or captured). Modelled on text: `Vertices[0]` is the top-left anchor, the box (`ImageWidth` x `ImageHeight`, page units)
  is rotated clockwise about it (`RotationDegrees`), `FillOpacity` is the picture's opacity (the toolbar's Opacity box), `FlipHorizontal/Vertical` mirror it within its box.
  The picture file is COPIED into `Jobs/<job>/images/<guid>.png|jpg` (`JobImageStore`) so it travels with the job; the annotation holds only the file name (`ImageFile`).
  Photos with an EXIF orientation are re-saved upright (`ImageGeometry.ExifUprightTransform`, tested against the spec); a PNG/JPEG already upright is copied byte for byte.
- Toolbar "Insert Picture" (also Ctrl+V with a picture on the clipboard, after text/our own format). Placed at its natural size on paper, shrunk to fit 80% of the page, centred on what
  is on screen, selected, `IncludeInPrint` ticked (a picture you place is meant to print; untick it for a preview-only aid).
- Core `ImageGeometry` (all tested): `Resize` (8 handles, any rotation, optional aspect lock; the opposite side never moves — 12,000 random drags), `HitHandle`, `FitInside`,
  `MirrorPicture` (mirroring FLIPS a picture, unlike text which stays readable — 36,000 points followed through the mirror), `PagePositionOfContent`.
- Overlay (MainWindow.xaml, in the per-annotation template, next to the text layers): picture + dashed box + 8 handle squares (drawn in XAML, picked up in code via
  `MainViewModel.HitImageHandle`, like the text width handle). Dragging a handle = `BeginImageResize/ResizeImageTo/EndImageResize` (works from a snapshot, no drift); dragging the body
  = the ordinary one-vertex drag. A selected picture is a SOLID area: anything higher in the stacking order at a click (text, a shape) wins over it, except on its handles.
- Picture options bar (floating, like the text one): Keep aspect ratio, Original proportions, Rotate (turns about the CENTRE; the anchor is moved to keep it). Color/Thickness/Fill are
  hidden while a picture is selected (`ShowShapeStyleControls`); Opacity and Include in Print remain.
- Printing: `DrawIncludedPolygonsOntoPage` composites a picture in stacking order with the shapes (`CompositeImageOntoPage` -> `ImageRenderer.RasterizeForPrint`, full-resolution file,
  the twin of `TextRenderer.RasterizeForPrint`); text still goes on top of everything.
- Clipboard: pasting pictures into ANOTHER job copies the file across (`CopyAsset`, using the payload's `SourceJobId`).
- Anything keyed per annotation KIND must handle Image: hit-testing (`FindPolygonNearPoint`, `IsPointNearPolygonLine`), bounds (`GetEntityBounds`), mirror, save/load mapping, print.

## Edit base image (ver_0.18.0)
- Right-click anywhere on the page > "Edit Base Image…" opens `BaseImageEditorWindow` on the CURRENT page's image (`_currentRawPage`). Turn by a precise angle (clockwise +; the canvas
  grows, new corners white; quarter turns are exact), crop with eight handles / move / draw-new / typed Left-Top-Width-Height, and the SAME `EffectsPanel` as the main window (own
  settings object, Auto-crop and Dock hidden via `HideAutoCropSection/HideDockToggle`). Undo/Redo (Ctrl+Z/Y) inside the editor; Save closes it; nothing on disk changes before Save.
- WYSIWYG order is effects -> turn -> crop, in the preview and in `BaseImageEditService.RenderAsync` (the preview runs on a <=1600 px copy with its DPI scaled down with it).
- `MainViewModel.ApplyBaseImageEditAsync`: writes the new image over the page file (PNG stays PNG, JPG stays JPG at the Export quality; DPI recorded; temp file then move), then moves
  EVERY annotation on the page in BOTH views to follow (`AnnotationEditTransform`, Core, tested): text boxes/pictures are rigid (anchor carried, rotation turns with the page);
  polygons/curves carry every point; ellipses keep centre and radii (swapped on a quarter turn). Nothing is ever deleted, even if the crop leaves it off the page.
  Coordinates: Before view = measured from the whole image; After view = from the AUTO-CROPPED image, so the old and new auto-crop origins are found first
  (`AutoCropOriginAsync` -> `IImageProcessingService.FindContentCropAsync`, which shares `ComputeContentCrop` with `CropToContentAsync` so the offset is exactly the one the crop takes).
- If the edit baked pixel effects in (`ImageEffectSettings.ChangesPixels`), the job's effect settings reset like a newly opened job (default preset or none); a turn/crop-only edit leaves
  them alone. The page's undo history is dropped (its states are in the old coordinates). No backup of the original is kept — Undo/Redo exist only inside the editor.
- The pixel warp (`BaseImageEditMath.WarpMatrix`) and the annotation mapping (`MapPixel`) are the same arithmetic. Checked against the real OpenCV `warpAffine` from Python: a marker lands
  within 0.007 px of the predicted spot; quarter turns equal `cv2.rotate`. OpenCvSharp names verified from its source (note `RotateFlags.Rotate90Counterclockwise`).

## Print dialog: page range and per-page settings (ver_0.19.0)
- The preview follows the PAGE RANGE. `MainViewModel._printPreviewPages` = `PrintPageRange.Resolve(All | Current | Selected, _printCurrentPageIndex, rangeText, pageCount)` (Core, tested); the page
  box, Previous/Next/First/Last and the "of N" count move through only those pages. "Current Page" = the page being previewed at the moment it is chosen (or the main window's page when the
  dialog opens), held in `_printCurrentPageIndex`. Typing in the range box rebuilds the list at once and re-renders after a 350 ms pause. An empty range shows "No pages match…".
- "Different settings for page N" (under the page navigation, Size mode): a `PagePrintOverride` (paper size, orientation, fit/scale %) for the PREVIEWED page, copied from the common settings when
  ticked. The preview sheet shows the effective orientation/scale (`PreviewIsLandscape/FitToPage/ScalePercent`). Printing stays ONE job: `IPrintingService.PrintTilesAsync(..., pageOverrides)` and
  `PrintDocument.QueryPageSettings` set paper and orientation just before each page. Margins, copies and page numbers stay common. `_pagePrintOverrides` is keyed by page INDEX, so it is re-keyed in
  `ReKeyPagesAsync` with the other per-page state, and cleared on `LoadJob`. (Split tiles have their own rows/columns override and no per-page paper settings.)
- Open Job (JobBrowserWindow): the preview pane has first/previous/page box/next/last (and the wheel) for a job's pages, like the Print dialog's.

## Style rule (ver_0.19.0)
- An annotation must always have an outline or a fill (`StyleRules`, Core). The toolbar offers "No Stroke"/"No Fill" on the strength of the FIRST selected item but the choice applies to ALL, so the
  setters guard each item (`StyleRules.AllowStrokeOff/AllowFillOff`); creation and load call `EnsureVisible`, which also repairs old files; and the Color/Fill getters only return an item the list is
  offering (a ComboBox given a selection it doesn't list shows blank and stays that way).

## Edit base image: zoom, pan, canvas growth, sizing (ver_0.19.0)
- The stage is a scrolling, zoomable view (`ViewScroll`/`StageHost`/`Stage`). Ctrl+wheel zooms at the pointer, wheel scrolls, Shift+wheel scrolls sideways (as the main window); pan with the middle button,
  Space+drag, or the Pan switch. All stage items are placed in CANVAS PIXELS and converted with `ScreenX/ScreenY` (`_stage` = the region drawn). The preview copy is re-made sharper as the zoom needs
  (`BaseImageEditMath.PreviewDimension`: 1600 / 3200 / 6400 / whole page).
- The crop may reach PAST the page: dragged into the grey pasteboard (`BaseImageEditMath.Pasteboard`), typed (negative Left/Top, larger Width/Height), or "Add white margin". The saved page is bigger and the
  extra is white (`BaseImageEditService.Crop` via `BaseImageEditMath.CropPlan`). Limits: `ClampCrop` (reach = as much again as the canvas's longer side; 60 MP or the original's size). The annotation
  transform already handles a negative crop origin.
- The window opens filling the usable height of the monitor it is on (`MonitorWorkArea`), in `Window_SourceInitialized` — UiScaling returns early at 100% and the authored size overflowed small screens.

## Edit base image: tools — Erase, Straighten, Skew (ver_0.20.0)
- Tool strip under the toolbar: Crop (default) / Erase / Straighten / Skew. Pipeline, in this order, in the preview AND in `BaseImageEditService.RenderAsync`: effects -> skew correction (perspective warp) ->
  turn by Angle -> crop -> erased areas painted white. A change to an earlier step (turn, skew) CLEARS the erased areas (they were drawn on the page as it was; Undo brings them back; the info line says so).
- Skew = perspective correction: four draggable corners on the page AS PHOTOGRAPHED (the stage shows the unturned image while the tool is open), Apply makes `PerspectiveStage` (Core: output size = longer
  opposite sides, homography solved from the four corners). `PerspectiveMath` is tested and was checked against the real OpenCV `warpPerspective` (a marker lands within 0.025 px; corners map exactly).
  Matrices handed to OpenCV go through `ToPixelCentreConvention` (OpenCV numbers pixels by centre). Corners must make a convex shape (`IsConvex`) and the result must fit the pixel ceiling.
- Annotations follow a skew: `AnnotationEditTransform` takes the `PerspectiveStage`; points go through the homography (polygons/curves exact); text and pictures keep their size, the anchor is carried and
  the rotation takes the direction the box's own top edge was turned to (a label along a tilted sheet's top edge comes out level — tested).
- Straighten = GIMP's "measure/straighten": drag a line along something that should be level or upright; `PerspectiveMath.StraightenAngle` (tested over 5000 random lines) gives the turn (never more than 45
  degrees; whichever of level/upright is nearer). The tool is used once, then back to Crop.
- Erase: `EraseRect` (fill a rectangle) and `EraseStroke` (round brush; thick anti-aliased lines + a dab at every point), Core records so undo history can share them. Recorded in turned-canvas pixels, painted
  AFTER the crop at (shape - crop origin), so areas outside the page are simply already white.
- The editor's undo state is (angle, crop, effects, skew quad, erasures). Save refuses while the Skew corners are open but unapplied. `BaseImageEditResult` carries `Skew` and `HasErasures`.

## UI changes in ver_0.20.0
- The main window's PAGE PREVIEW scrollbars are 12 wide (scoped `ScrollViewer.Resources` style based on the app's slim one); everything else stays 8.
- Title bar: logo + "Clean Print - <job>" centred between the tools and the caption buttons (a DockPanel so long messages still trim). The status after loading a page is just the job id.
- Lists: CMS geometry (header padding 4, rows 2, text size unchanged), column rules in the header, and ALL colours from the theme (tints of `AppIconBrush` over `AppPanelBackgroundBrush`, so they follow
  Light/Dark and the Icon colour setting). Note: JobBrowserWindow/TextInputDialog have no theme background, so in Dark mode the list is dark inside a light dialog — theme those windows if that matters.
- Effects panel: right margin 4 (was 12) since the scrollbar is slim.
- Print dialog: the per-page settings are collapsed behind a header button (default closed) and absent for a one-page job.

## Small UI rules (ver_0.20.1)
- "Include in Print" shows and works for a MULTI-selection (acting on all of it). Ticked only when everything it acts on is included. If ordinary shapes and Hide Row/Column bands are selected together the box acts
  on the shapes only (`IncludeInPrintTargets`) — for a band the same flag means "apply this hide to the printout" and has its own "Hide in Print" box, shown when only bands are selected.
- Title bar: the two outer columns share a width (`SharedSizeGroup="TitleBarSide"`), so the centred logo + title is the middle of the WINDOW. The caption buttons are right-aligned inside their (equal-width) column.
- Effects panel: only the scrollbar sits close to the right edge (gap 4). The preset row and the Undock / Reset buttons keep 12 from the edge like the left side (`SetDockedState` works out the remainder,
  allowing for the docked splitter strip).

## Themes (ver_0.21.0) — modelled on the CMS application's
- Palettes live in CORE (`CleanPrint.Core/Theming`): `ThemeSettings` (a dozen base colours + corner radii + IsDark/Classic), `ThemeCatalog` (Classic = the old grey look and the default; Modern, Emerald, Teal,
  Violet, Rose, Amber, Slate = the CMS light themes; Dark and Darker = neutral Photoshop-style greys with a blue accent), `ColourMath` (Mix, Luminance, Contrast, BestTextOn, Shade). Tested as real C#: every
  theme's text/secondary text/accent label/focus ring/border contrast, and that dark themes have a dark window and the canvas is the darkest surface.
- `App/Theming/ThemeManager.Apply(name)` works every brush out of a palette and writes it into `Application.Resources` (so it wins over the merged LightTheme.xaml fallback): `Theme*` brushes (what
  Themes/InputStyles.xaml uses) and the older `App*` names (window, panel, preview canvas, chrome, text, icons, scroll thumbs, sliders). Radii: `RadiusControl/Check/Card/TabTop/Page`.
  `ApplyIconColor` keeps the Icon colour setting separate, so a theme change never loses it. App listens for `MainViewModel.ThemeName` changes and re-applies, so it is live.
- `Themes/InputStyles.xaml`: implicit styles for Button (default button = accent), ToggleButton, CheckBox, RadioButton, GroupBox (title above, card below), ToolTip, TextBox, ComboBox (also EDITABLE: the DPI box),
  ComboBoxItem, TabControl/TabItem, ContextMenu/MenuItem/Separator, plus keyed `SpinButtonStyle` and `NumberTextBoxStyle` (NumericUpDown). Adapted from the CMS; its ComboBox was not editable-capable, ours is.
  RULE: a control with its OWN explicit Style loses these — give it `BasedOn="{StaticResource {x:Type Button}}"` (done for ToolButton/ToolToggleButton/SettingGroup and the swatch combos' ComboBoxItem styles). Small
  fixed-size buttons/toggles need `Padding="0"` (the default padding is 12,3). Define each implicit style ONCE (see the scroll bar lesson above).
- Every window sets Background/Foreground from the theme (an implicit Window style would NOT apply to derived window classes). Lists (`AppListView` etc.) use Theme* tokens, not icon-colour tints.
- Settings: "Dark mode" checkbox replaced by a Theme combo (`MainViewModel.ThemeOptions`/`SelectedThemeOption`, swatches). `AppSettings.Theme` stores the name; `DarkMode` is still written (true for a dark theme)
  and read only when Theme is empty (`ThemeCatalog.Resolve`), so old settings files migrate (Dark mode on -> Dark, off -> Classic).
- Not themed: system MessageBoxes (Windows draws them). Not built: the CMS's custom-theme editor (user-defined colours).

## Highlight strength and the Dark palette (ver_0.22.3)
- The highlight (`Selection`: selected list row, drop-down/menu item under the pointer, a toggle that is on) was a faint tint in every light theme. Light themes now DERIVE it: 40% from the accent towards white
  (`ThemeCatalog.Light`), Classic a solid mid grey (#ABABAB). Tests require it to be clearly darker than white AND the theme's text to stay readable on it (4.5:1) for every theme.
- Dark is a lighter medium-dark grey (window #3E3E3E, panels #454545, toolbars #333333, preview background #525252, inputs inset #303030, text #E0E0E0); Darker is the near-black one. Dark's selection (#2B63A6) is
  the one blue that is visible on the lighter panel (>= 1.5:1) while the text on it still reads (>= 4.5:1): the band is narrow, so re-run Tests/CoreChecks after touching those colours.
- Settings > General order: Theme, Icon colour (slider), Toolbar scale, Dialog & panel scale.

## Text frame (ver_0.21.0)
- A rectangle round a text annotation, behind the letters, with its own line style (Solid/Dash/Dot/Dash-dot/Long dash — `FrameLineStyles.DashArray`, in line-widths, used by both the screen's StrokeDashArray and
  print's DashStyle), colour, width (page units; 0 = no line), fill (or none), opacity (whole frame, separate from the text's), padding and corner radius (points). Model: `PolygonAnnotation.Frame*` (old files = no frame).
  View model: `PolygonViewModel.Frame*` (+ on-screen `FrameDisplay*` set in `UpdateTextDisplay`); toolbar: `CurrentFrame*` through `ChangeTextStyle`, in the text options bar (each label grouped with its control).
  Print: `TextRenderer.RasterizeFrameForPrint`, composited under the letters. The selection rectangle, width handle and hit-test box move out by `FrameOutsetDiu` (padding + half the line).

## Colour combos: self-correcting (ver_0.21.0)
- The Color/Fill boxes were occasionally left EMPTY (after double-clicking a text, editing, clicking away). The root cause was not found by reading (every path ends consistent), so `MainWindow.SyncStyleCombos`
  re-asserts the answer with SetCurrentValue after any change (ContextIdle), under `MainViewModel.SuppressStyleWrites` so the correction isn't taken for a choice. If it recurs, this is the place to log.

## Testing, now in the repo (ver_0.22.0)
- `Tests/CoreChecks` (run `dotnet run` inside it): the Core's pure logic as real C# — themes (every palette's contrast), icon colour, perspective/skew incl. corners OUTSIDE the image, straighten, text frame model,
  silent-export naming/formats/settings, private-job paths. It compiles the Core sources straight in (net8.0), so it needs no Windows workload. `Tests/verify_static.py` (python3, from the repo root) is the static
  checker for the WPF side, which can't be compiled off Windows: XAML parses, handlers exist, bindings exist, resource keys defined (XAML or ThemeManager), template TargetNames, duplicate implicit styles, missing
  `using`s for WPF types, bracket balance. Run both before packaging. (Earlier sessions' larger test file lived in /tmp and was lost in a sandbox reset — that is why these are in the repo now.)

## Icon colour and accent labels (ver_0.22.0)
- The Icon colour selector is gone. Icons use `ThemeCatalog.IconColour(theme, lightness)`: the theme's own icon colour (light grey #C8C8C8 on Dark/Darker; a tint of the accent on the light themes), shifted by the
  Settings slider `IconLightness` (-100..+100, saved in AppSettings; the old `IconColor` is ignored), and nudged back if it would drop under 3:1 against the toolbar — so icons can never vanish.
  `ThemeManager.SetIconLightness` re-colours live. A button filled with the accent has a WHITE label (`ColourMath.LabelOn`), black only on a very pale accent.

## Skew past the photograph (ver_0.22.0)
- Skew corners may leave the image (up to `BaseImageEditMath.MaxReach`); the stage grows to hold them (`StageRect(canvas, QuadBounds(_skewWork))`, re-laid-out when a drag ends). Whatever the corrected page takes from
  outside the photo is white (the warp's border value). The maths needs nothing special: a homography takes any four corners.

## Silent export (ver_0.22.0)
- Core/Export: `SilentExportSettings` (+ service, file `silent-export.json`, read FRESH per job by the Watcher), `ImageFormats` (Manual = png/jpg for the Export dialogs; Silent adds bmp/tif), `SilentExportNaming`
  (tokens {date} {time} {datetime} {job} {page} {pages}; -NN added for multi-page jobs without {page}; Windows-invalid characters and reserved names made safe on EVERY OS; `UniquePath` never overwrites).
- `CleanPrint.Watcher/Services/SilentImageExporter` saves each captured page (copy if already the right format, else OpenCV decode/encode IN MEMORY — OpenCV's path-based ImRead/ImWrite can fail on non-English
  Windows paths). Pages are saved as captured: Effects are NOT applied (they belong to the window being skipped). In Program.cs's JobReady handler: if enabled -> export, don't open the App (if nothing was
  exported, fall back to opening it); the job stays in the cache unless "keep job" is off or private mode is on.
- OpenCvSharp is pinned at 4.10 (Watcher and App): check API shapes against THAT version, not the library's current source. Known difference: `Cv2.ImEncode` returns void in 4.10 (a bool in newer
  releases), so never use its return value — test the output buffer instead (this broke the 0.22.1 Watcher build).
- `ExportQualitySettings.DefaultExportFormat` ("png"/"jpg") sets the Save dialogs' FilterIndex (MainViewModel.DefaultExportFilterIndex).

## Private mode (ver_0.22.0) — like a private browser window, for jobs
- Session-only on purpose (always starts off). While on: a captured job is MOVED out of the cache (`PrivateJobService.AdoptIntoPrivate`, App.LoadJobAndActivate), blank jobs and opened images are CREATED in the private
  area (`CleanPrintPaths.NewJobFolder(id, isPrivate)`). Private area = `%TEMP%\CleanPrint-Private`, ids start `private-`. EVERY place that needs a job's folder must use `CleanPrintPaths.JobFolder(jobId)` (never
  `Path.Combine(JobsRoot, id)`): it maps private ids to the private area. Private jobs never appear in Open Job / cache figures (those list Jobs only).
- Deleted: when LoadJob moves to another job (`PrivateJobService.DeleteFolder`, retries for ~10 s since a preview may still hold a file), at App exit and at App start (`PurgeAll`; also clears the marker file).
- The Watcher is a separate process: the App keeps `private-mode.flag` (CleanPrintPaths.PrivateModeFlagPath) while the mode is on; the Watcher honours it only if the App is really running (a crash can leave a stale
  file) — with silent export on, a job captured then is exported and its cache copy deleted. UI: title-bar toggle (`FlatIconToggleButton`, `IconPrivate`), a PRIVATE tag by the title, and a purple strip under the title
  bar (fixed `PrivateModeBrush`, readable on every theme) shown whenever the mode is on OR the job on screen is private (it stays private after the mode is switched off, until you leave it).
- Known: opening an ordinary job from Open Job while in private mode edits that ordinary job (it is not copied); not restricted.

## All features requested so far are built
Slim scrollbars, mirror, new blank job with page management, pictures on a page, and edit base image. Possible follow-ups the user has not asked for: zoom inside the editor; a one-level
"undo the base image edit" in the main window (today undo exists only inside the editor); multi-select insert of pictures.
