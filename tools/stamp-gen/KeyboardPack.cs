using SkiaSharp;

namespace StampGen;

/// <summary>
/// Keyboard keycaps (original artwork) in light and dark themes: skirt with vertical bevel gradient, dished top face with inner
/// highlight, layered soft shadow; legends are Fira Sans outlines, modifier glyphs are our own paths (the Windows-style key uses a
/// generic window glyph, not a trademark logo).
/// </summary>
internal static class KeyboardPack
{
    private const double U = 128;

    private sealed record Theme(string Key, L Name, string SkirtTop, string SkirtBottom, string Outline, double OutlineOpacity, string FaceTop, string FaceBottom,
        double Dish, double Rim, string Legend, string Secondary, double ShadowOpacity);

    private static readonly Theme LightTheme = new("light", T.Light, "#E9ECF0", "#B4BAC4", "#7D8592", 0.55, "#FFFFFF", "#EEF0F3", 0.85, 0.9, "#363C46", "#6B7380", 0.32);
    private static readonly Theme DarkTheme = new("dark", T.Dark, "#30353D", "#0F1114", "#8A929E", 0.3, "#434952", "#2A2E35", 0.07, 0.16, "#F1F3F6", "#AEB5C0", 0.42);

    internal enum Icon { None, Shift, Caps, Tab, Enter, Backspace, Up, Down, Left, Right, Cmd, Option, Window }

    private sealed record Key(string Stem, L Name, string? Main, string? Shifted, Icon Icon, double Width, bool Iso = false, bool Small = false, bool IconTop = false);

    private static readonly L KeyName = new("{0} key", "Taste {0}", "Tecla {0}", "Touche {0}", "{0}キー", "Tecla {0}");
    private static L N(string label) => KeyName.F(L.Same(label));

    private static List<Key> Keys()
    {
        var k = new List<Key>();
        for (var c = 'A'; c <= 'Z'; c++) k.Add(new($"key-{char.ToLowerInvariant(c)}", N(c.ToString()), c.ToString(), null, Icon.None, 1));
        var shifted = ")!@#$%^&*(";
        for (var d = 1; d <= 10; d++) { var n = d % 10; k.Add(new($"key-{n}", N(n.ToString()), n.ToString(), shifted[n].ToString(), Icon.None, 1)); }
        foreach (var (stem, main, sh, en) in new[]
        {
            ("backtick", "`", "~", "Backtick"), ("minus", "-", "_", "Minus"), ("equals", "=", "+", "Equals"), ("bracket-left", "[", "{", "Left bracket"),
            ("bracket-right", "]", "}", "Right bracket"), ("backslash", "\\", "|", "Backslash"), ("semicolon", ";", ":", "Semicolon"), ("quote", "'", "\"", "Quote"),
            ("comma", ",", "<", "Comma"), ("period", ".", ">", "Period"), ("slash", "/", "?", "Slash"),
        })
            k.Add(new($"key-{stem}", T.Paren(N(main), L.Same(en)), main, sh, Icon.None, 1));
        for (var f = 1; f <= 24; f++) k.Add(new($"key-f{f}", N("F" + f), "F" + f, null, Icon.None, 1, Small: true));
        k.Add(new("key-esc", new("Esc key", "Esc-Taste", "Tecla Esc", "Touche Échap", "Escキー", "Tecla Esc"), "Esc", null, Icon.None, 1, Small: true));
        k.Add(new("key-tab", new("Tab key", "Tabulatortaste", "Tecla Tab", "Touche Tab", "Tabキー", "Tecla Tab"), "Tab", null, Icon.Tab, 1.5));
        k.Add(new("key-caps-lock", new("Caps Lock key", "Feststelltaste", "Bloq Mayús", "Verr. Maj", "Caps Lockキー", "Caps Lock"), "Caps Lock", null, Icon.Caps, 1.75));
        k.Add(new("key-shift-left", new("Left Shift key", "Umschalttaste links", "Mayús izquierda", "Maj gauche", "左Shiftキー", "Shift esquerdo"), "Shift", null, Icon.Shift, 2.25));
        k.Add(new("key-shift-right", new("Right Shift key", "Umschalttaste rechts", "Mayús derecha", "Maj droite", "右Shiftキー", "Shift direito"), "Shift", null, Icon.Shift, 2.75));
        k.Add(new("key-shift", new("Shift key", "Umschalttaste", "Tecla Mayús", "Touche Maj", "Shiftキー", "Tecla Shift"), null, null, Icon.Shift, 1));
        k.Add(new("key-ctrl", new("Ctrl key", "Strg-Taste", "Tecla Ctrl", "Touche Ctrl", "Ctrlキー", "Tecla Ctrl"), "Ctrl", null, Icon.None, 1.25));
        k.Add(new("key-alt", new("Alt key", "Alt-Taste", "Tecla Alt", "Touche Alt", "Altキー", "Tecla Alt"), "Alt", null, Icon.None, 1.25));
        k.Add(new("key-win", new("Windows-style key", "Fenstertaste", "Tecla de ventana", "Touche fenêtre", "ウィンドウキー", "Tecla de janela"), null, null, Icon.Window, 1.25));
        k.Add(new("key-cmd", new("Command key", "Befehlstaste", "Tecla Comando", "Touche Commande", "Commandキー", "Tecla Command"), "cmd", null, Icon.Cmd, 1.25, IconTop: true));
        k.Add(new("key-option", new("Option key", "Wahltaste", "Tecla Opción", "Touche Option", "Optionキー", "Tecla Option"), "option", null, Icon.Option, 1.25, IconTop: true));
        k.Add(new("key-fn", new("Fn key", "Fn-Taste", "Tecla Fn", "Touche Fn", "Fnキー", "Tecla Fn"), "Fn", null, Icon.None, 1, Small: true));
        k.Add(new("key-space", new("Space bar", "Leertaste", "Barra espaciadora", "Barre d’espace", "スペースキー", "Barra de espaço"), null, null, Icon.None, 5));
        k.Add(new("key-enter", new("Enter key", "Eingabetaste", "Tecla Intro", "Touche Entrée", "Enterキー", "Tecla Enter"), "Enter", null, Icon.Enter, 2.25));
        k.Add(new("key-enter-iso", new("Enter key (ISO)", "Eingabetaste (ISO)", "Tecla Intro (ISO)", "Touche Entrée (ISO)", "Enterキー（ISO）", "Tecla Enter (ISO)"), "Enter", null, Icon.Enter, 1.5, Iso: true));
        k.Add(new("key-backspace", new("Backspace key", "Rücktaste", "Tecla Retroceso", "Touche Retour arrière", "Backspaceキー", "Tecla Backspace"), "Backspace", null, Icon.Backspace, 2));
        k.Add(new("key-delete", new("Delete key", "Entf-Taste", "Tecla Supr", "Touche Suppr", "Deleteキー", "Tecla Delete"), "Delete", null, Icon.None, 1, Small: true));
        k.Add(new("key-insert", new("Insert key", "Einfg-Taste", "Tecla Insert", "Touche Inser", "Insertキー", "Tecla Insert"), "Insert", null, Icon.None, 1, Small: true));
        k.Add(new("key-home", new("Home key", "Pos1-Taste", "Tecla Inicio", "Touche Origine", "Homeキー", "Tecla Home"), "Home", null, Icon.None, 1, Small: true));
        k.Add(new("key-end", new("End key", "Ende-Taste", "Tecla Fin", "Touche Fin", "Endキー", "Tecla End"), "End", null, Icon.None, 1, Small: true));
        k.Add(new("key-page-up", new("Page Up key", "Bild-auf-Taste", "Tecla Re Pág", "Touche Page préc.", "Page Upキー", "Tecla Page Up"), "PgUp", null, Icon.None, 1, Small: true));
        k.Add(new("key-page-down", new("Page Down key", "Bild-ab-Taste", "Tecla Av Pág", "Touche Page suiv.", "Page Downキー", "Tecla Page Down"), "PgDn", null, Icon.None, 1, Small: true));
        k.Add(new("key-print-screen", new("Print Screen key", "Druck-Taste", "Tecla Impr Pant", "Touche Impr. écran", "PrintScreenキー", "Tecla Print Screen"), "PrtSc", null, Icon.None, 1, Small: true));
        k.Add(new("key-up", new("Up arrow key", "Pfeiltaste oben", "Flecha arriba", "Flèche haut", "上矢印キー", "Seta para cima"), null, null, Icon.Up, 1));
        k.Add(new("key-down", new("Down arrow key", "Pfeiltaste unten", "Flecha abajo", "Flèche bas", "下矢印キー", "Seta para baixo"), null, null, Icon.Down, 1));
        k.Add(new("key-left", new("Left arrow key", "Pfeiltaste links", "Flecha izquierda", "Flèche gauche", "左矢印キー", "Seta para a esquerda"), null, null, Icon.Left, 1));
        k.Add(new("key-right", new("Right arrow key", "Pfeiltaste rechts", "Flecha derecha", "Flèche droite", "右矢印キー", "Seta para a direita"), null, null, Icon.Right, 1));
        return k;
    }

    public static Pack Build()
    {
        var pack = new Pack("keyboard", 40, new L("Keyboard keys", "Tastaturtasten", "Teclas", "Touches du clavier", "キーボードのキー", "Teclas do teclado"));
        L keyKw = new("key", "Taste", "tecla", "touche", "キー", "tecla");
        L kbKw = new("keyboard", "Tastatur", "teclado", "clavier", "キーボード", "teclado");
        L shortcutKw = new("shortcut", "Tastenkürzel", "atajo", "raccourci", "ショートカット", "atalho");
        foreach (var theme in new[] { LightTheme, DarkTheme })
            foreach (var key in Keys())
            {
                var extra = new List<L> { keyKw, kbKw, shortcutKw, theme.Name, L.Same("keycap") };
                if (key.Main is not null) extra.Add(L.Same(key.Main));
                if (key.Shifted is not null) extra.Add(L.Same(key.Shifted));
                pack.Add($"{key.Stem}-{theme.Key}", Draw(key, theme), T.Paren(key.Name, theme.Name), extra.ToArray());
            }
        return pack;
    }

    private static string Draw(Key key, Theme t)
    {
        var W = key.Width * U;
        var H = key.Iso ? 2 * U : U;
        var s = new SvgDoc(W, H);
        const double m = 7, top = 5, bottom = 13;
        SKPath skirt, face;
        if (!key.Iso)
        {
            skirt = Geo.RRect(m, top, W - 2 * m, H - top - bottom, 18);
            face = Geo.RRect(m + 9, top + 4, W - 2 * m - 18, H - top - bottom - 4 - 17, 13);
        }
        else
        {
            using var a = Geo.RRect(m, top, W - 2 * m, U - 2, 18);
            using var b = Geo.RRect(m + 0.25 * U, top, W - 2 * m - 0.25 * U, H - top - bottom, 18);
            skirt = Geo.Union(a, b);
            using var fa = Geo.RRect(m + 9, top + 4, W - 2 * m - 18, U - 2 - 4 - 10, 13);
            using var fb = Geo.RRect(m + 0.25 * U + 9, top + 4, W - 2 * m - 0.25 * U - 18, H - top - bottom - 4 - 17, 13);
            face = Geo.Union(fa, fb);
        }
        var sb = skirt.TightBounds; var fbnd = face.TightBounds;
        s.Shadow(skirt, 0, 5, 8, t.ShadowOpacity);
        s.Path(skirt, s.V(sb.Top, sb.Bottom, new Stop(0, t.SkirtTop), new Stop(1, t.SkirtBottom)));
        // side walls: slightly darker band towards the lower corners
        s.Path(skirt, s.Linear(sb.Left, 0, sb.Right, 0, new Stop(0, "#000000", 0.06), new Stop(0.12, "#000000", 0), new Stop(0.88, "#000000", 0), new Stop(1, "#000000", 0.08)));
        s.StrokePath(skirt, t.Outline, 1.2, t.OutlineOpacity);
        using (var rim = Geo.TopRim(skirt, 2.2)) s.Path(rim, "#FFFFFF", t.Rim * 0.8);
        // top face
        using (var faceShadow = Geo.Translate(face, 0, 2.5)) s.Path(faceShadow, "#000000", t.Key == "light" ? 0.07 : 0.25);
        s.Path(face, s.V(fbnd.Top, fbnd.Bottom, new Stop(0, t.FaceTop), new Stop(1, t.FaceBottom)));
        var dishR = Math.Max(fbnd.Width, fbnd.Height) * 0.62;
        s.Path(face, s.Radial(fbnd.MidX, fbnd.Top + Math.Min(fbnd.Height, U) * 0.32, dishR, new[] { new Stop(0, "#FFFFFF", t.Dish), new Stop(1, "#FFFFFF", 0) }, 1, 0.75));
        using (var fr = Geo.TopRim(face, 1.6)) s.Path(fr, "#FFFFFF", t.Rim);
        using (var fl = Geo.BottomRim(face, 1.6)) s.Path(fl, "#000000", t.Key == "light" ? 0.06 : 0.3);

        // legends live in the first 1u row of the face (ISO Enter: its top part)
        var fx = fbnd.Left + 10; var fy = fbnd.Top + 8; var fw = fbnd.Width - 20; var fh = Math.Min(fbnd.Height, U - top - bottom - 21) - 16;
        Legends(s, key, t, fx, fy, fw, fh);
        skirt.Dispose(); face.Dispose();
        return s.ToString();
    }

    private static void Legends(SvgDoc s, Key key, Theme t, double fx, double fy, double fw, double fh)
    {
        var ink = t.Legend;
        var wide = key.Width >= 1.5;
        if (key.Shifted is not null && key.Main is not null)
        {
            using var sh = Txt.Fit(key.Shifted, "fira-medium", 31, fx, fy - 1, fw, fh / 2);
            using var mn = Txt.Fit(key.Main, "fira-medium", 31, fx, fy + fh / 2 + 1, fw, fh / 2);
            s.Path(sh, t.Secondary); s.Path(mn, ink);
            return;
        }
        if (key.Icon == Icon.None && key.Main is not null)
        {
            var size = key.Main.Length == 1 ? 46 : key.Small ? 25 : 27;
            using var tx = Txt.Fit(key.Main, "fira-medium", size, fx, fy, fw, fh, align: key.Small || wide ? 0.5 : 0.5);
            s.Path(tx, ink);
            return;
        }
        if (key.Icon == Icon.None) return; // space bar
        if (key.Main is null)
        {
            // icon only, centred
            var sz = key.Icon is Icon.Up or Icon.Down or Icon.Left or Icon.Right ? 40 : 38;
            using var ic = IconPath(key.Icon, fx + (fw - sz) / 2, fy + (fh - sz) / 2, sz);
            s.Path(ic, ink);
            return;
        }
        if (key.IconTop)
        {
            // Mac-style: glyph top-right, word bottom-left
            using var ic = IconPath(key.Icon, fx + fw - 26, fy, 26);
            s.Path(ic, ink);
            using var tx = Txt.Fit(key.Main, "fira-medium", 20, fx, fy + fh - 22, fw, 22, align: 0);
            s.Path(tx, ink);
            return;
        }
        // icon + word, side by side (wide keys) or stacked
        if (wide)
        {
            const double isz = 30;
            using var probe = Txt.Raw(key.Main, "fira-medium", 24);
            var tw = Math.Min(probe.TightBounds.Width, fw - isz - 12);
            var total = isz + 10 + tw;
            var x0 = fx + (fw - total) / 2;
            using var ic = IconPath(key.Icon, x0, fy + (fh - isz) / 2, isz);
            s.Path(ic, ink);
            using var tx = Txt.Fit(key.Main, "fira-medium", 24, x0 + isz + 10, fy, tw, fh, align: 0);
            s.Path(tx, ink);
        }
        else
        {
            using var ic = IconPath(key.Icon, fx + (fw - 28) / 2, fy, 28);
            s.Path(ic, ink);
            using var tx = Txt.Fit(key.Main, "fira-medium", 18, fx, fy + fh - 20, fw, 20);
            s.Path(tx, ink);
        }
    }

    /// <summary>Our own modifier glyphs on a 24-unit grid, stroked to outlines and placed at (x, y) with the given size.</summary>
    public static SKPath IconPath(Icon icon, double x, double y, double size)
    {
        var p = new SKPath();
        var w = 2.0;
        switch (icon)
        {
            case Icon.Shift:
            case Icon.Caps:
                p.MoveTo(12, 2.5f); p.LineTo(21.5f, 12); p.LineTo(16, 12); p.LineTo(16, icon == Icon.Caps ? 17 : 21); p.LineTo(8, icon == Icon.Caps ? 17 : 21); p.LineTo(8, 12); p.LineTo(2.5f, 12); p.Close();
                if (icon == Icon.Caps) { p.MoveTo(8, 21); p.LineTo(16, 21); }
                break;
            case Icon.Tab:
                p.MoveTo(2, 12); p.LineTo(19, 12); p.MoveTo(13.5f, 6.5f); p.LineTo(19, 12); p.LineTo(13.5f, 17.5f); p.MoveTo(22, 5.5f); p.LineTo(22, 18.5f);
                break;
            case Icon.Enter:
                p.MoveTo(20.5f, 4); p.LineTo(20.5f, 13.5f); p.LineTo(4, 13.5f); p.MoveTo(9.5f, 8); p.LineTo(4, 13.5f); p.LineTo(9.5f, 19);
                break;
            case Icon.Backspace:
                p.MoveTo(8, 5); p.LineTo(22, 5); p.LineTo(22, 19); p.LineTo(8, 19); p.LineTo(1.5f, 12); p.Close();
                p.MoveTo(11.5f, 8.5f); p.LineTo(18, 15.5f); p.MoveTo(18, 8.5f); p.LineTo(11.5f, 15.5f);
                break;
            case Icon.Up: case Icon.Down: case Icon.Left: case Icon.Right:
                p.MoveTo(12, 21); p.LineTo(12, 4); p.MoveTo(5, 10.5f); p.LineTo(12, 3.5f); p.LineTo(19, 10.5f);
                w = 2.6;
                var rot = icon switch { Icon.Down => 180f, Icon.Left => -90f, Icon.Right => 90f, _ => 0f };
                p.Transform(SKMatrix.CreateRotationDegrees(rot, 12, 12));
                break;
            case Icon.Cmd:
                foreach (var (cx, cy) in new[] { (6.5f, 6.5f), (17.5f, 6.5f), (6.5f, 17.5f), (17.5f, 17.5f) }) p.AddCircle(cx, cy, 3.5f);
                p.MoveTo(10, 6.5f); p.LineTo(10, 17.5f); p.MoveTo(14, 6.5f); p.LineTo(14, 17.5f);
                p.MoveTo(6.5f, 10); p.LineTo(17.5f, 10); p.MoveTo(6.5f, 14); p.LineTo(17.5f, 14);
                w = 1.8;
                break;
            case Icon.Option:
                p.MoveTo(2.5f, 6.5f); p.LineTo(8.5f, 6.5f); p.LineTo(15.5f, 17.5f); p.LineTo(21.5f, 17.5f); p.MoveTo(14, 6.5f); p.LineTo(21.5f, 6.5f);
                break;
            case Icon.Window:
            {
                var r = new SKPath();
                r.AddRoundRect(new SKRect(3, 4, 21, 20), 2.5f, 2.5f);
                using var outline = Geo.Stroke(r, 2);
                using var bar = Geo.RRect(3, 4, 18, 5, 2);
                using var u = Geo.Union(outline, bar);
                r.Dispose();
                var res = Geo.Transform(u, Place(x, y, size));
                return res;
            }
        }
        using var st = Geo.Stroke(p, w);
        p.Dispose();
        return Geo.Transform(st, Place(x, y, size));
    }

    private static SKMatrix Place(double x, double y, double size)
    {
        var k = (float)(size / 24);
        return SKMatrix.CreateScale(k, k).PostConcat(SKMatrix.CreateTranslation((float)x, (float)y));
    }
}
