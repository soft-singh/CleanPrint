using CleanPrint.Core.Models;
using CleanPrint.Core.Theming;
using CleanPrint.Core.Export;
using CleanPrint.Core.Paths;

int fails = 0, passes = 0;
void C(bool ok, string what) { if (ok) { passes++; Console.WriteLine("PASS  " + what); } else { fails++; Console.WriteLine("FAIL  " + what); } }
double Near(double a, double b) => Math.Abs(a - b);
GeoPoint G(double x, double y) => new(x, y);

// ======================= themes: colour maths and every palette =======================
{
    C(Rgb.Parse("#1E90FF", Rgb.Black) == new Rgb(0x1E, 0x90, 0xFF) && Rgb.Parse("1e90ff", Rgb.Black) == new Rgb(0x1E, 0x90, 0xFF) && Rgb.Parse("#FFF", Rgb.Black) == Rgb.White && Rgb.Parse("#FF112233", Rgb.Black) == new Rgb(0x11, 0x22, 0x33), "colours parse: #RRGGBB, without #, #RGB, and #AARRGGBB (alpha ignored)");
    C(Rgb.Parse("nonsense", Rgb.White) == Rgb.White && Rgb.Parse("", Rgb.White) == Rgb.White && Rgb.Parse(null, Rgb.White) == Rgb.White, "unparseable text gives the fallback");
    C(ColourMath.Mix(Rgb.Black, Rgb.White, 0) == Rgb.Black && ColourMath.Mix(Rgb.Black, Rgb.White, 1) == Rgb.White && ColourMath.Mix(Rgb.Black, Rgb.White, 0.5) == new Rgb(128, 128, 128), "Mix: 0 is the first colour, 1 the second, half is the middle");
    C(Near(ColourMath.Contrast(Rgb.Black, Rgb.White), 21) < 1e-6 && Near(ColourMath.Contrast(Rgb.White, Rgb.White), 1) < 1e-9, "contrast: black on white is 21:1, a colour on itself 1:1");
    C(ThemeCatalog.Resolve("", false) == "Classic" && ThemeCatalog.Resolve(null, true) == "Dark" && ThemeCatalog.Resolve("violet", true) == "Violet" && ThemeCatalog.Resolve("NoSuchTheme", false) == "Classic", "an old settings file's Dark mode switch becomes Dark or Classic; a saved name wins (any case); an unknown name falls back to Classic");
    C(ThemeCatalog.All.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ThemeCatalog.All.Count && ThemeCatalog.All[0].Name == "Classic", $"{ThemeCatalog.All.Count} themes, every name different, Classic first");

    foreach (var info in ThemeCatalog.All)
    {
        var t = info.Create(); string n = info.Name;
        Rgb R(string h) => Rgb.Parse(h, Rgb.Black);
        bool allValid = new[] { t.Accent, t.Window, t.Chrome, t.Page, t.Border, t.Header, t.Selection, t.Tab, t.Canvas, t.Input, t.Text }.All(h => Rgb.Parse(h, new Rgb(1, 2, 3)) != new Rgb(1, 2, 3));
        C(allValid, $"{n}: every colour is a valid hex value");
        Rgb text = R(t.Text), window = R(t.Window), page = R(t.Page), input = R(t.Input), chrome = R(t.Chrome), sel = R(t.Selection), header = R(t.Header), accent = R(t.Accent), border = R(t.Border), canvas = R(t.Canvas);
        double worstText = new[] { ColourMath.Contrast(text, window), ColourMath.Contrast(text, page), ColourMath.Contrast(text, input), ColourMath.Contrast(text, chrome), ColourMath.Contrast(text, header), ColourMath.Contrast(text, sel) }.Min();
        C(worstText >= 4.5, $"{n}: text reads on the window, panel, input, toolbar, header AND selection (worst {worstText:0.0}:1, bar 4.5)");
        var second = ColourMath.Mix(text, window, 0.41);
        double sec = Math.Min(ColourMath.Contrast(second, window), ColourMath.Contrast(second, page));
        C(sec >= 3.0, $"{n}: secondary (dimmer) text still reads ({sec:0.0}:1, bar 3)");
        var label = ColourMath.LabelOn(accent); double onAccent = ColourMath.Contrast(label, accent);
        C(onAccent >= 3.0 && label == Rgb.White, $"{n}: the label on an accent-filled button is WHITE and reads ({onAccent:0.0}:1, bar 3)");
        double ring = ColourMath.Contrast(accent, page);
        C(ring >= 2.0, $"{n}: the accent stands out against panels, so a focus ring is visible ({ring:0.0}:1, bar 2)");
        double edge = Math.Max(ColourMath.Contrast(border, page), ColourMath.Contrast(border, window));
        C(edge >= 1.3, $"{n}: borders are visible against the surfaces ({edge:0.00}:1)");
        double winLum = ColourMath.Luminance(window);
        C(t.IsDark ? winLum < 0.15 : winLum > 0.5, $"{n}: a {(t.IsDark ? "dark" : "light")} theme has a {(t.IsDark ? "dark" : "light")} window");
        if (t.IsDark)
        {
            C(ColourMath.Luminance(input) <= ColourMath.Luminance(page), $"{n}: inputs are inset (no lighter than the panel), like Photoshop");
            C(ColourMath.Luminance(canvas) < 0.20 && ColourMath.Contrast(Rgb.White, canvas) >= 4.0, $"{n}: the preview background is dark but not black, and a white page stands out from it ({ColourMath.Contrast(Rgb.White, canvas):0.0}:1)");
        }
        // The highlight (selected row / drop-down / menu item) must be clearly visible against the surface it sits on, not a faint tint — and the text on it must still read.
        double selVisible = Math.Max(ColourMath.Contrast(sel, page), ColourMath.Contrast(sel, window));
        C(selVisible >= (t.IsDark ? 1.5 : 1.7), $"{n}: the highlight stands out from the panel ({selVisible:0.00}:1)");
    }
}

// ======================= icon colour: a variation of the theme, with a lightness slider =======================
{
    C(ColourMath.LabelOn(new Rgb(0xFF, 0xF1, 0x76)) == Rgb.Black && ColourMath.LabelOn(new Rgb(0x2D, 0x8C, 0xEB)) == Rgb.White && ColourMath.LabelOn(new Rgb(0xD9, 0x77, 0x06)) == Rgb.White, "a very pale accent gets a black label; blue and amber get white");
    var dark = ThemeCatalog.Find("Dark").Create();
    C(ThemeCatalog.IconColour(dark, 0) == new Rgb(0xC8, 0xC8, 0xC8), $"the Dark theme's default icon colour is light grey #C8C8C8 (got {ThemeCatalog.IconColour(dark, 0).ToHex()})");
    var classic = ThemeCatalog.Find("Classic").Create();
    C(ThemeCatalog.IconColour(classic, 0) == new Rgb(0x59, 0x59, 0x59), "Classic keeps its medium-dark grey");
    var vi = ThemeCatalog.IconColour(ThemeCatalog.Find("Violet").Create(), 0);
    C(vi.B > vi.G && vi != ThemeCatalog.IconColour(classic, 0), $"a coloured theme tints its icons with its accent ({vi.ToHex()})");
    foreach (var info in ThemeCatalog.All)
    {
        var t = info.Create(); var chrome = Rgb.Parse(t.Chrome, Rgb.Black);
        bool readable = true; double worst = 99;
        foreach (int v in new[] { -100, -75, -50, -25, 0, 25, 50, 75, 100 })
        {
            double contrast = ColourMath.Contrast(ThemeCatalog.IconColour(t, v), chrome); worst = Math.Min(worst, contrast);
            if (contrast < 3.0) readable = false;
        }
        C(readable, $"{info.Name}: icons stay readable against the toolbar at every slider position (worst {worst:0.0}:1)");
        var mid = new[] { -20, 0, 20 }.Select(v => ColourMath.Luminance(ThemeCatalog.IconColour(t, v))).ToArray();
        C(mid[0] <= mid[1] + 1e-9 && mid[1] <= mid[2] + 1e-9, $"{info.Name}: sliding towards lighter never makes icons darker");
    }
    C(ThemeCatalog.IconColour(dark, 1000) == ThemeCatalog.IconColour(dark, 100) && ThemeCatalog.IconColour(dark, -1000) == ThemeCatalog.IconColour(dark, -100), "slider values outside -100..100 are clamped");
}

// ======================= perspective (skew) correction and straighten =======================
{
    var rect = new Quad(G(100, 50), G(500, 50), G(500, 350), G(100, 350));
    C(PerspectiveMath.OutputSize(rect) == (400, 300), "a 400 x 300 rectangle gives a 400 x 300 page");
    var rnd = new Random(8); bool corners = true; double worstCorner = 0;
    for (int t = 0; t < 3000; t++)
    {
        var q = new Quad(G(30 + rnd.NextDouble() * 80, 20 + rnd.NextDouble() * 60), G(400 + rnd.NextDouble() * 90, 10 + rnd.NextDouble() * 70), G(380 + rnd.NextDouble() * 110, 300 + rnd.NextDouble() * 80), G(10 + rnd.NextDouble() * 90, 310 + rnd.NextDouble() * 70));
        if (!PerspectiveMath.IsConvex(q)) continue;
        var (w, h) = PerspectiveMath.OutputSize(q); var H = PerspectiveMath.Homography(q, w, h);
        var want = new[] { G(0, 0), G(w, 0), G(w, h), G(0, h) }; var src = q.Points;
        for (int i = 0; i < 4; i++) { var got = PerspectiveMath.Apply(H, src[i]); double d = Math.Sqrt((got.X - want[i].X) * (got.X - want[i].X) + (got.Y - want[i].Y) * (got.Y - want[i].Y)); worstCorner = Math.Max(worstCorner, d); if (d > 1e-6) corners = false; }
    }
    C(corners, $"over 3000 random shapes each corner goes exactly to its corner of the corrected page (worst {worstCorner:E1} px)");

    // THE QUAD MAY REACH PAST THE IMAGE: corners with negative or too-large coordinates are just as valid
    var beyond = new Quad(G(-120, -80), G(1150, -60), G(1210, 900), G(-90, 940));   // an 1000 x 800 image; every corner outside it
    var (bw, bh) = PerspectiveMath.OutputSize(beyond); var bH = PerspectiveMath.Homography(beyond, bw, bh);
    var back = new[] { beyond.TopLeft, beyond.TopRight, beyond.BottomRight, beyond.BottomLeft }.Select(p => PerspectiveMath.Apply(bH, p)).ToArray();
    C(PerspectiveMath.IsConvex(beyond) && Near(back[0].X, 0) < 1e-6 && Near(back[2].X, bw) < 1e-6 && Near(back[2].Y, bh) < 1e-6, "a quad whose corners lie OUTSIDE the image (negative / past the edge) is a valid correction, and its corners still map to the page's corners");
    var partly = new Quad(G(-50, 40), G(900, 20), G(950, 780), G(30, 830));
    var inv = PerspectiveMath.Invert(PerspectiveMath.Homography(partly, PerspectiveMath.OutputSize(partly).Width, PerspectiveMath.OutputSize(partly).Height));
    var corner = PerspectiveMath.Apply(inv, G(0, 0));
    C(Near(corner.X, -50) < 1e-6 && Near(corner.Y, 40) < 1e-6, "the page's top-left maps back to a point outside the image: the white margin comes from beyond it");
    C(PerspectiveMath.IsConvex(rect) && !PerspectiveMath.IsConvex(new Quad(G(0, 0), G(100, 100), G(100, 0), G(0, 100))) && !PerspectiveMath.IsConvex(new Quad(G(0, 0), G(50, 0), G(100, 0), G(0, 100))), "a rectangle is fine; a bow-tie and a flat shape are refused");

    C(Near(PerspectiveMath.StraightenAngle(G(0, 0), G(100, 10)), -5.7106) < 1e-3 && Near(PerspectiveMath.StraightenAngle(G(100, 10), G(0, 0)), PerspectiveMath.StraightenAngle(G(0, 0), G(100, 10))) < 1e-9, "straighten: a line sloping down 10 in 100 is turned back by 5.71 degrees, whichever way it was drawn");
    var rr = new Random(3); bool ok = true;
    for (int t = 0; t < 5000; t++)
    {
        var pa = G(rr.NextDouble() * 500, rr.NextDouble() * 500); var pb = G(rr.NextDouble() * 500, rr.NextDouble() * 500); if (Math.Abs(pa.X - pb.X) + Math.Abs(pa.Y - pb.Y) < 1) continue;
        double turn = PerspectiveMath.StraightenAngle(pa, pb); double dir = Math.Atan2(pb.Y - pa.Y, pb.X - pa.X) * 180 / Math.PI + turn; double mod = ((dir % 90) + 90) % 90;
        if (Math.Min(mod, 90 - mod) > 1e-7 || Math.Abs(turn) > 45.0000001) ok = false;
    }
    C(ok, "straighten: over 5000 random lines the line ends exactly level or upright, never turned more than 45 degrees");
}

// ======================= text frame model =======================
{
    C(FrameLineStyles.DashArray(FrameLineStyle.Solid).Length == 0 && FrameLineStyles.DashArray(FrameLineStyle.Dash).SequenceEqual(new[] { 4.0, 3.0 }), "frame line styles: solid has no pattern, dashed is 4/3");
    var old = System.Text.Json.JsonSerializer.Deserialize<PolygonAnnotation>("{\"Kind\":1,\"Text\":\"hi\"}")!;
    C(!old.FrameVisible && old.FrameFillColorHex is null && old.FrameOpacity == 100, "a file from before frames has no frame");
}


// ======================= silent export: names, formats, settings =======================
{
    var now = new DateTime(2026, 10, 29, 14, 5, 33);
    C(SilentExportNaming.Build("", now, "job1", 1, 1) == "CleanPrint-2026-10-29-14-05-33" && SilentExportNaming.Build("   ", now, "job1", 1, 1) == "CleanPrint-2026-10-29-14-05-33", "an empty name pattern means the default: CleanPrint-{date}-{time}");
    C(SilentExportNaming.Build("Scan {date}", now, "job1", 1, 1) == "Scan 2026-10-29", "a one-page job uses the pattern as typed ({date} filled in)");
    C(SilentExportNaming.Build("{DATE}_{Time}", now, "j", 1, 1) == "2026-10-29_14-05-33", "tokens are case-insensitive");
    C(SilentExportNaming.Build("{datetime}-{job}", now, "155307-2026", 1, 1) == "2026-10-29_14-05-33-155307-2026", "{datetime} and {job}");
    C(SilentExportNaming.Build("Report", now, "j", 2, 12) == "Report-02" && SilentExportNaming.Build("Report", now, "j", 11, 12) == "Report-11", "several pages and no {page} in the pattern: -NN is added, padded to the width of the page count");
    C(SilentExportNaming.Build("Report", now, "j", 3, 5) == "Report-3", "…and a single digit when there are fewer than ten pages");
    C(SilentExportNaming.Build("P{page}of{pages}", now, "j", 4, 9) == "P4of9", "{page} and {pages} where the user put them (no extra suffix then)");
    var names = Enumerable.Range(1, 12).Select(p => SilentExportNaming.Build("Doc", now, "j", p, 12)).ToList();
    C(names.Distinct().Count() == 12, "the twelve pages of a job get twelve different names");
    C(SilentExportNaming.Build("a/b\\c:d*e?f\"g<h>i|j", now, "j", 1, 1) == "a_b_c_d_e_f_g_h_i_j", "characters a file name can't hold become underscores");
    C(SilentExportNaming.Build("CON", now, "j", 1, 1) == "CON_" && SilentExportNaming.Build("nul", now, "j", 1, 1) == "nul_" && SilentExportNaming.Build("COM3", now, "j", 1, 1) == "COM3_", "names Windows reserves are made safe");
    C(SilentExportNaming.Build("...", now, "j", 1, 1) == "CleanPrint" && SilentExportNaming.Build("  name. . ", now, "j", 1, 1) == "name", "trailing dots/spaces are dropped; nothing left means CleanPrint");
    C(SilentExportNaming.Build(new string('x', 400), now, "j", 1, 1).Length <= 150, "an absurdly long name is cut short");
    C(SilentExportNaming.Build("{unknown}", now, "j", 1, 1) == "{unknown}", "an unknown token is left as it was typed");

    string tmp = Path.Combine(Path.GetTempPath(), "cleanprint-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
    try
    {
        string p1 = SilentExportNaming.UniquePath(tmp, "scan", ".png"); File.WriteAllText(p1, "x");
        string p2 = SilentExportNaming.UniquePath(tmp, "scan", ".png"); File.WriteAllText(p2, "x");
        string p3 = SilentExportNaming.UniquePath(tmp, "scan", ".png");
        C(Path.GetFileName(p1) == "scan.png" && Path.GetFileName(p2) == "scan (2).png" && Path.GetFileName(p3) == "scan (3).png", "an export never overwrites an earlier one: scan.png, scan (2).png, scan (3).png");
    }
    finally { Directory.Delete(tmp, true); }

    C(ImageFormats.Find(ImageFormats.Silent, "TIF").Extension == ".tif" && ImageFormats.Find(ImageFormats.Silent, "tiff").Id == "tif" && ImageFormats.Find(ImageFormats.Silent, ".jpeg").Id == "jpg" && ImageFormats.Find(ImageFormats.Silent, "weird").Id == "png" && ImageFormats.Find(ImageFormats.Silent, null).Id == "png", "format lookup: any case, jpeg/tiff spellings, a leading dot; unknown or missing = the first (PNG)");
    C(ImageFormats.Manual.Select(f => f.Id).SequenceEqual(new[] { "png", "jpg" }) && ImageFormats.Silent.Select(f => f.Id).SequenceEqual(new[] { "png", "jpg", "bmp", "tif" }), "Export offers PNG and JPEG; silent export also BMP and TIFF");

    var settings = new SilentExportSettings { Enabled = true, Folder = "D:\\Scans", Format = "tif", NamePattern = "S-{date}", KeepJob = false };
    var back = System.Text.Json.JsonSerializer.Deserialize<SilentExportSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
    C(back.Enabled && back.Folder == "D:\\Scans" && back.Format == "tif" && back.NamePattern == "S-{date}" && !back.KeepJob, "every silent export setting survives save and load");
    var fresh = new SilentExportSettings();
    C(!fresh.Enabled && fresh.KeepJob && fresh.Format == "png" && fresh.NamePattern == SilentExportNaming.DefaultPattern, "silent export is OFF by default, keeps the job, PNG, default name");
    C(new SilentExportSettings { Folder = "" }.EffectiveFolder.EndsWith("CleanPrint") && new SilentExportSettings { Folder = " C:\\Out " }.EffectiveFolder == "C:\\Out", "an empty folder means Pictures\\CleanPrint; a typed one is trimmed");
    Environment.SetEnvironmentVariable("CP_CHECK_DIR", "Z:\\Expanded");
    C(new SilentExportSettings { Folder = "%CP_CHECK_DIR%\\x" }.EffectiveFolder == "Z:\\Expanded\\x", "environment variables in the folder are expanded");
    C(new ExportQualitySettings().DefaultExportFormat == "png", "the default export format is PNG");
}

// ======================= private mode: where jobs live =======================
{
    C(CleanPrintPaths.IsPrivateJobId("private-155307") && CleanPrintPaths.IsPrivateJobId("PRIVATE-x") && !CleanPrintPaths.IsPrivateJobId("155307-2026") && !CleanPrintPaths.IsPrivateJobId(null) && !CleanPrintPaths.IsPrivateJobId("my-private-job"), "a job id is private only if it STARTS with private-");
    C(CleanPrintPaths.JobFolder("private-abc") == Path.Combine(CleanPrintPaths.PrivateJobsRoot, "private-abc") && CleanPrintPaths.JobFolder("abc") == Path.Combine(CleanPrintPaths.JobsRoot, "abc"), "a private job's folder is in the private area, any other job's in the job cache");
    C(!CleanPrintPaths.PrivateJobsRoot.StartsWith(CleanPrintPaths.JobsRoot, StringComparison.OrdinalIgnoreCase) && !CleanPrintPaths.JobsRoot.StartsWith(CleanPrintPaths.PrivateJobsRoot, StringComparison.OrdinalIgnoreCase), "the private area is nowhere inside the job cache (and the cache is not inside it)");
    C(CleanPrintPaths.PrivateModeFlagPath.StartsWith(CleanPrintPaths.LocalAppDataRoot), "the private-mode marker file sits in the CleanPrint data folder (where the Watcher can see it)");
    string a = CleanPrintPaths.NewJobFolder("Blank-A4-20261029-1", isPrivate: true);
    string b = CleanPrintPaths.NewJobFolder("Blank-A4-20261029-1", isPrivate: true);
    try
    {
        C(Directory.Exists(a) && Directory.Exists(b) && a != b && Path.GetFileName(a).StartsWith("private-") && Path.GetDirectoryName(a) == CleanPrintPaths.PrivateJobsRoot, "new private jobs get their own folders in the private area, with the private prefix, even in the same second");
        C(Path.GetFileName(b) == Path.GetFileName(a) + "-2", "the second one in the same second is numbered");
        C(CleanPrintPaths.JobFolder(Path.GetFileName(a)) == a, "JobFolder finds a job NewJobFolder made");
    }
    finally { try { Directory.Delete(CleanPrintPaths.PrivateJobsRoot, true); } catch { } }
}


// ======================= the dark theme is medium-dark, not near-black =======================
{
    Rgb R(string h) => Rgb.Parse(h, Rgb.Black);
    var dark = ThemeCatalog.Find("Dark").Create(); var darker = ThemeCatalog.Find("Darker").Create();
    C(ColourMath.Luminance(R(dark.Window)) > ColourMath.Luminance(R("#323232")) && ColourMath.Luminance(R(dark.Canvas)) > ColourMath.Luminance(R("#1E1E1E")) * 2, "Dark is lighter than the first version: its window and (much more) its preview background");
    C(ColourMath.Luminance(R(dark.Canvas)) > ColourMath.Luminance(R(darker.Canvas)) && ColourMath.Luminance(R(dark.Window)) > ColourMath.Luminance(R(darker.Window)), "Dark stays lighter than Darker, which is the near-black one");
    foreach (var name in new[] { "Modern", "Emerald", "Teal", "Violet", "Rose", "Amber", "Slate", "Classic" })
    {
        var t = ThemeCatalog.Find(name).Create(); var sel = R(t.Selection);
        C(ColourMath.Contrast(sel, Rgb.White) >= 1.7, $"{name}: the highlight is clearly darker than white (was a faint tint) - {ColourMath.Contrast(sel, Rgb.White):0.0}:1 against white");
    }
    C(ThemeCatalog.Find("Modern").Create().Selection != "#A9CBEE", "Modern's highlight is no longer the pale #A9CBEE");
}

//@@NEW-CHECKS@@

Console.WriteLine($"\n{passes} passed, {fails} failed");
Console.WriteLine(fails == 0 ? "ALL PASSED" : "SOME FAILED");
return fails == 0 ? 0 : 1;
