using SkiaSharp;

namespace StampGen;

/// <summary>Interface: mouse actions, touch gestures, UI controls (light + dark) and cursors. All original artwork.</summary>
internal static class InterfacePack
{
    private static readonly Hue Accent = new("accent", "#A39AFF", "#6A5CFA", "#4F40E6", "#3326A8", "#FFFFFF");

    private sealed record Surface(string Key, L Name, Hue Body, string Field, string FieldBorder, string Text, string Muted, string Track, string TrackLo);

    private static readonly Surface LightS = new("light", T.Light, Pal.White, "#FFFFFF", "#AEB5C0", "#262B33", "#B7BDC7", "#DDE1E7", "#C4CAD3");
    private static readonly Surface DarkS = new("dark", T.Dark, Pal.Charcoal, "#2B3038", "#5B6370", "#F1F3F6", "#69717E", "#4A515C", "#363C45");

    public static Pack Build()
    {
        var pack = new Pack("interface", 41, new L("Interface", "Benutzeroberfläche", "Interfaz", "Interface", "インターフェース", "Interface"));
        Mouse(pack);
        Gestures(pack);
        Controls(pack);
        Cursors(pack);
        return pack;
    }

    // ------------------------------------------------------------------ mouse

    private static void Mouse(Pack pack)
    {
        L mouse = new("mouse", "Maus", "ratón", "souris", "マウス", "mouse");
        L click = new("click", "Klick", "clic", "clic", "クリック", "clique");
        var actions = new (string Key, L Name, string Hl, int Extra)[]
        {
            ("mouse", new("Mouse", "Maus", "Ratón", "Souris", "マウス", "Mouse"), "", 0),
            ("mouse-left-click", new("Left click", "Linksklick", "Clic izquierdo", "Clic gauche", "左クリック", "Clique esquerdo"), "L", 1),
            ("mouse-right-click", new("Right click", "Rechtsklick", "Clic derecho", "Clic droit", "右クリック", "Clique direito"), "R", 2),
            ("mouse-middle-click", new("Middle click", "Mittelklick", "Clic central", "Clic central", "中クリック", "Clique do meio"), "W", 3),
            ("mouse-scroll-up", new("Scroll up", "Nach oben scrollen", "Desplazar arriba", "Défiler vers le haut", "上にスクロール", "Rolar para cima"), "W", 4),
            ("mouse-scroll-down", new("Scroll down", "Nach unten scrollen", "Desplazar abajo", "Défiler vers le bas", "下にスクロール", "Rolar para baixo"), "W", 5),
            ("mouse-drag", new("Drag", "Ziehen", "Arrastrar", "Glisser", "ドラッグ", "Arrastar"), "L", 6),
        };
        foreach (var sf in new[] { LightS, DarkS })
            foreach (var (key, name, hl, extra) in actions)
                pack.Add($"{key}-{sf.Key}", MouseSvg(sf, hl, extra), T.Paren(name, sf.Name), mouse, click, sf.Name);
    }

    private static string MouseSvg(Surface sf, string hl, int extra)
    {
        var s = new SvgDoc(128, 128);
        var ox = extra is 4 or 5 or 6 ? -12.0 : 0;
        if (extra == 6)
        {
            // motion trail on the left
            for (var i = 0; i < 3; i++)
            {
                using var l = Geo.Open(new List<(double, double)> { (6 + i * 3, 46 + i * 18), (26 + i * 2, 46 + i * 18) });
                using var st = Geo.Stroke(l, 5);
                s.Path(st, Accent.Mid, 0.55 - i * 0.12);
            }
        }
        using var body = Geo.RRect(36 + ox, 10, 56, 108, 28, 28, 26, 26);
        Art.Glossy(s, body, sf.Body, gloss: 0.9);
        var b = body.TightBounds;
        using var split = Geo.Rect(b.Left, b.Top, b.Width, 46);
        using var top = Geo.Inter(body, split);
        if (hl is "L" or "R")
        {
            using var half = Geo.Rect(hl == "L" ? b.Left : b.MidX, b.Top, b.Width / 2, 46);
            using var btn0 = Geo.Inter(top, half);
            using var btn = Geo.Deflate(btn0, 2.5);
            var bb = btn.TightBounds;
            s.Path(btn, s.V(bb.Top, bb.Bottom, new Stop(0, Accent.Hi), new Stop(1, Accent.Lo)));
            using var rim = Geo.TopRim(btn, 1.5); s.Path(rim, "#FFFFFF", 0.6);
        }
        // seams
        using (var seam = Geo.Open(new List<(double, double)> { (b.MidX, b.Top + 2), (b.MidX, b.Top + 46) })) s.StrokePath(seam, sf.Body.Edge, 1.6, 0.8);
        using (var seam2 = Geo.Open(Art.Arc(b.MidX, b.Top + 34, b.Width / 2 - 1.5, 12, 15, 165).Select(p => p).ToList())) s.StrokePath(seam2, sf.Body.Edge, 1.6, 0.8);
        // wheel
        using var wheel = Geo.RRect(b.MidX - 5, b.Top + 14, 10, 22, 5);
        var wheelHi = hl == "W";
        s.Path(wheel, s.V(b.Top + 14, b.Top + 36, new Stop(0, wheelHi ? Accent.Hi : "#5B6370"), new Stop(1, wheelHi ? Accent.Lo : "#20242A")));
        using (var wr = Geo.TopRim(wheel, 1.2)) s.Path(wr, "#FFFFFF", 0.5);
        if (extra is 1 or 2 or 3)
        {
            // click burst near the pressed button
            var cx = extra == 1 ? b.Left + 4 : extra == 2 ? b.Right - 4 : b.MidX;
            var dirs = extra == 1 ? new[] { 200.0, 235, 270 } : extra == 2 ? new[] { 270.0, 305, 340 } : new[] { 235.0, 270, 305 };
            foreach (var a in dirs)
            {
                var r = a * Math.PI / 180;
                using var l = Geo.Open(new List<(double, double)> { (cx + Math.Cos(r) * 8, b.Top + 8 + Math.Sin(r) * 8), (cx + Math.Cos(r) * 18, b.Top + 8 + Math.Sin(r) * 18) });
                using var st = Geo.Stroke(l, 4.5);
                s.Path(st, Pal.Orange.Mid);
            }
        }
        if (extra is 4 or 5)
        {
            for (var i = 0; i < 2; i++)
            {
                var y = extra == 4 ? 40 + i * 22 : 66 + i * 22;
                var pts = extra == 4 ? new List<(double, double)> { (96, y + 10), (108, y - 2), (120, y + 10) } : new List<(double, double)> { (96, y - 10), (108, y + 2), (120, y - 10) };
                using var l = Geo.Open(pts);
                using var st = Geo.Stroke(l, 7);
                s.Path(st, s.V(y - 12, y + 12, new Stop(0, Accent.Hi), new Stop(1, Accent.Lo)), i == 0 ? 1 : 0.6);
            }
        }
        if (extra == 6)
        {
            using var arrow = Art.Arrow(Art.Line(98, 64, 124, 64), 7, 14, 18);
            s.Path(arrow, Accent.Mid);
        }
        return s.ToString();
    }

    // ------------------------------------------------------------------ gestures

    private static void Touch(SvgDoc s, double x, double y, int rings = 2, double r = 15)
    {
        for (var i = rings; i >= 1; i--)
        {
            using var c = Geo.Circle(x, y, r + i * 9);
            s.StrokePath(c, Accent.Mid, 3, 0.45 / i);
        }
        s.GroundShadow(x, y + 4, r + 4, r + 2, 0.25);
        using var dot = Geo.Circle(x, y, r);
        s.Path(dot, s.Radial(x - r * 0.3, y - r * 0.35, r * 1.4, new[] { new Stop(0, Accent.Hi), new Stop(0.6, Accent.Mid), new Stop(1, Accent.Lo) }), 0.92);
        using var ring = Geo.Circle(x, y, r - 1.5);
        s.StrokePath(ring, "#FFFFFF", 2.5, 0.8);
        using var hl = Geo.Ellipse(x - r * 0.25, y - r * 0.4, r * 0.45, r * 0.25);
        s.Path(hl, "#FFFFFF", 0.55);
    }

    private static void Trail(SvgDoc s, double x0, double y0, double x1, double y1, double w = 26)
    {
        using var l = Geo.Open(new List<(double, double)> { (x0, y0), (x1, y1) });
        using var st = Geo.Stroke(l, w);
        s.Path(st, s.Linear(x0, y0, x1, y1, new Stop(0, Accent.Mid, 0), new Stop(1, Accent.Mid, 0.45)));
    }

    private static void SmallArrow(SvgDoc s, (double, double) a, (double, double) b)
    {
        using var p = Art.Arrow(new List<(double X, double Y)> { a, b }, 6, 14, 18);
        s.Path(p, Pal.Charcoal.Mid, 0.85);
    }

    private static void Gestures(Pack pack)
    {
        L gesture = new("gesture", "Geste", "gesto", "geste", "ジェスチャー", "gesto");
        L touch = new("touch", "Touch", "táctil", "tactile", "タッチ", "toque");
        var list = new (string Key, L Name, Action<SvgDoc> Draw)[]
        {
            ("gesture-tap", new("Tap", "Tippen", "Tocar", "Toucher", "タップ", "Tocar"), s => Touch(s, 64, 64, 2)),
            ("gesture-double-tap", new("Double tap", "Doppeltippen", "Doble toque", "Double toucher", "ダブルタップ", "Toque duplo"), s =>
            {
                Touch(s, 60, 68, 3);
                using var badge = Geo.Circle(100, 28, 17);
                Art.Glossy(s, badge, Pal.Orange, 0.6);
                using var t = Txt.Fit("2×", "fira-bold", 20, 86, 18, 28, 20);
                s.Path(t, "#FFFFFF");
            }),
            ("gesture-long-press", new("Long press", "Lange drücken", "Mantener pulsado", "Appui long", "長押し", "Pressionar e segurar"), s =>
            {
                Touch(s, 64, 64, 0);
                using var arc = Geo.Open(Art.Arc(64, 64, 32, 32, -90, 200));
                using var st = Geo.Stroke(arc, 8);
                s.Path(st, s.Linear(64, 32, 40, 80, new Stop(0, Accent.Hi), new Stop(1, Accent.Lo)));
                using var bg = Geo.Open(Art.Arc(64, 64, 32, 32, 200, 270));
                s.StrokePath(bg, Accent.Mid, 8, 0.2, cap: "butt");
            }),
            ("gesture-swipe-left", new("Swipe left", "Nach links wischen", "Deslizar a la izquierda", "Balayer vers la gauche", "左にスワイプ", "Deslizar para a esquerda"), s => { Trail(s, 112, 70, 50, 70); Touch(s, 50, 70, 0); SmallArrow(s, (40, 36), (8, 36)); }),
            ("gesture-swipe-right", new("Swipe right", "Nach rechts wischen", "Deslizar a la derecha", "Balayer vers la droite", "右にスワイプ", "Deslizar para a direita"), s => { Trail(s, 16, 70, 78, 70); Touch(s, 78, 70, 0); SmallArrow(s, (88, 36), (120, 36)); }),
            ("gesture-swipe-up", new("Swipe up", "Nach oben wischen", "Deslizar hacia arriba", "Balayer vers le haut", "上にスワイプ", "Deslizar para cima"), s => { Trail(s, 58, 114, 58, 52); Touch(s, 58, 52, 0); SmallArrow(s, (100, 44), (100, 10)); }),
            ("gesture-swipe-down", new("Swipe down", "Nach unten wischen", "Deslizar hacia abajo", "Balayer vers le bas", "下にスワイプ", "Deslizar para baixo"), s => { Trail(s, 58, 14, 58, 76); Touch(s, 58, 76, 0); SmallArrow(s, (100, 84), (100, 118)); }),
            ("gesture-pinch-in", new("Pinch in", "Zusammenziehen", "Pellizcar para alejar", "Pincer", "ピンチイン", "Pinçar"), s =>
            {
                Touch(s, 40, 88, 0); Touch(s, 88, 40, 0);
                SmallArrow(s, (14, 114), (34, 94)); SmallArrow(s, (114, 14), (94, 34));
            }),
            ("gesture-pinch-out", new("Pinch out", "Aufziehen", "Separar para ampliar", "Écarter", "ピンチアウト", "Afastar"), s =>
            {
                Touch(s, 50, 78, 0); Touch(s, 78, 50, 0);
                SmallArrow(s, (34, 94), (8, 120)); SmallArrow(s, (94, 34), (120, 8));
            }),
            ("gesture-rotate", new("Rotate", "Drehen", "Girar", "Pivoter", "回転", "Girar"), s =>
            {
                using var a1 = Art.Arrow(Art.Arc(64, 64, 50, 50, 200, 255), 6, 14, 18);
                using var a2 = Art.Arrow(Art.Arc(64, 64, 50, 50, 20, 75), 6, 14, 18);
                s.Path(a1, Pal.Charcoal.Mid, 0.85); s.Path(a2, Pal.Charcoal.Mid, 0.85);
                Touch(s, 40, 84, 0); Touch(s, 88, 44, 0);
            }),
            ("gesture-two-finger-scroll", new("Two-finger scroll", "Mit zwei Fingern scrollen", "Desplazar con dos dedos", "Défilement à deux doigts", "2本指スクロール", "Rolar com dois dedos"), s =>
            {
                Trail(s, 44, 112, 44, 64, 22); Trail(s, 84, 112, 84, 64, 22);
                Touch(s, 44, 64, 0, 13); Touch(s, 84, 64, 0, 13);
                SmallArrow(s, (64, 40), (64, 6));
            }),
        };
        foreach (var (key, name, draw) in list)
        {
            var s = new SvgDoc(128, 128);
            draw(s);
            pack.Add(key, s.ToString(), name, gesture, touch);
        }
    }

    // ------------------------------------------------------------------ controls

    private static void Controls(Pack pack)
    {
        L control = new("control", "Steuerelement", "control", "contrôle", "コントロール", "controle");
        L ui = new("ui", "UI", "IU", "IU", "UI", "IU");
        var items = new (string Key, L Name, double W, double H, Action<SvgDoc, Surface> Draw)[]
        {
            ("toggle-on", new("Toggle on", "Schalter an", "Interruptor activado", "Interrupteur activé", "トグル オン", "Alternância ligada"), 128, 96, (s, sf) => Toggle(s, sf, true)),
            ("toggle-off", new("Toggle off", "Schalter aus", "Interruptor desactivado", "Interrupteur désactivé", "トグル オフ", "Alternância desligada"), 128, 96, (s, sf) => Toggle(s, sf, false)),
            ("checkbox-checked", new("Checkbox checked", "Kontrollkästchen aktiviert", "Casilla marcada", "Case cochée", "チェックボックス オン", "Caixa marcada"), 128, 128, (s, sf) => Checkbox(s, sf, 1)),
            ("checkbox-unchecked", new("Checkbox unchecked", "Kontrollkästchen leer", "Casilla sin marcar", "Case non cochée", "チェックボックス オフ", "Caixa desmarcada"), 128, 128, (s, sf) => Checkbox(s, sf, 0)),
            ("checkbox-indeterminate", new("Checkbox indeterminate", "Kontrollkästchen unbestimmt", "Casilla indeterminada", "Case indéterminée", "チェックボックス 中間", "Caixa indeterminada"), 128, 128, (s, sf) => Checkbox(s, sf, 2)),
            ("radio-on", new("Radio button selected", "Optionsfeld ausgewählt", "Botón de opción seleccionado", "Bouton radio sélectionné", "ラジオボタン オン", "Botão de opção selecionado"), 128, 128, (s, sf) => Radio(s, sf, true)),
            ("radio-off", new("Radio button", "Optionsfeld", "Botón de opción", "Bouton radio", "ラジオボタン オフ", "Botão de opção"), 128, 128, (s, sf) => Radio(s, sf, false)),
            ("button-primary", new("Primary button", "Primäre Schaltfläche", "Botón principal", "Bouton principal", "プライマリボタン", "Botão principal"), 192, 96, (s, sf) => Button(s, sf, true)),
            ("button-secondary", new("Secondary button", "Sekundäre Schaltfläche", "Botón secundario", "Bouton secondaire", "セカンダリボタン", "Botão secundário"), 192, 96, (s, sf) => Button(s, sf, false)),
            ("text-field", new("Text field", "Textfeld", "Campo de texto", "Champ de texte", "テキストフィールド", "Campo de texto"), 224, 80, (s, sf) => Field(s, sf, 0)),
            ("search-field", new("Search field", "Suchfeld", "Campo de búsqueda", "Champ de recherche", "検索フィールド", "Campo de pesquisa"), 224, 80, (s, sf) => Field(s, sf, 1)),
            ("dropdown", new("Drop-down list", "Dropdown-Liste", "Lista desplegable", "Liste déroulante", "ドロップダウン", "Lista suspensa"), 224, 80, (s, sf) => Field(s, sf, 2)),
            ("slider", new("Slider", "Schieberegler", "Control deslizante", "Curseur", "スライダー", "Controle deslizante"), 224, 64, Slider),
            ("progress-bar", new("Progress bar", "Fortschrittsbalken", "Barra de progreso", "Barre de progression", "プログレスバー", "Barra de progresso"), 224, 48, Progress),
            ("spinner", new("Loading spinner", "Ladeanzeige", "Indicador de carga", "Indicateur de chargement", "読み込み中", "Indicador de carregamento"), 128, 128, Spinner),
            ("button-close", new("Close button", "Schließen-Schaltfläche", "Botón cerrar", "Bouton fermer", "閉じるボタン", "Botão fechar"), 128, 128, (s, sf) => Caption(s, sf, 0)),
            ("button-minimize", new("Minimize button", "Minimieren-Schaltfläche", "Botón minimizar", "Bouton réduire", "最小化ボタン", "Botão minimizar"), 128, 128, (s, sf) => Caption(s, sf, 1)),
            ("button-maximize", new("Maximize button", "Maximieren-Schaltfläche", "Botón maximizar", "Bouton agrandir", "最大化ボタン", "Botão maximizar"), 128, 128, (s, sf) => Caption(s, sf, 2)),
            ("window-controls", new("Window controls", "Fenstersteuerung", "Controles de ventana", "Contrôles de fenêtre", "ウィンドウ操作ボタン", "Controles da janela"), 224, 80, WindowControls),
            ("menu-hamburger", new("Menu button", "Menüschaltfläche", "Botón de menú", "Bouton menu", "メニューボタン", "Botão de menu"), 128, 128, (s, sf) => IconButton(s, sf, 0)),
            ("menu-kebab", new("More options (vertical)", "Weitere Optionen (vertikal)", "Más opciones (vertical)", "Plus d’options (vertical)", "その他（縦）", "Mais opções (vertical)"), 128, 128, (s, sf) => IconButton(s, sf, 1)),
            ("menu-meatballs", new("More options (horizontal)", "Weitere Optionen (horizontal)", "Más opciones (horizontal)", "Plus d’options (horizontal)", "その他（横）", "Mais opções (horizontal)"), 128, 128, (s, sf) => IconButton(s, sf, 2)),
            ("notification-badge", new("Notification badge", "Benachrichtigungsabzeichen", "Insignia de notificación", "Badge de notification", "通知バッジ", "Selo de notificação"), 128, 128, Bell),
        };
        foreach (var sf in new[] { LightS, DarkS })
            foreach (var (key, name, w, h, draw) in items)
            {
                var s = new SvgDoc(w, h);
                draw(s, sf);
                pack.Add($"{key}-{sf.Key}", s.ToString(), T.Paren(name, sf.Name), control, ui, sf.Name);
            }
    }

    private static void Knob(SvgDoc s, double x, double y, double r)
    {
        using var c = Geo.Circle(x, y, r);
        s.Shadow(c, 0, 2.5, 5, 0.35);
        s.Path(c, s.V(y - r, y + r, new Stop(0, "#FFFFFF"), new Stop(1, "#E3E6EB")));
        using var rim = Geo.BottomRim(c, 1.5); s.Path(rim, "#000000", 0.12);
    }

    private static void Track(SvgDoc s, SKPath p, Surface sf, bool on)
    {
        var b = p.TightBounds;
        if (on) Art.Glossy(s, p, Accent, 0.6, 0.03, 0.6);
        else
        {
            s.Path(p, s.V(b.Top, b.Bottom, new Stop(0, sf.TrackLo), new Stop(1, sf.Track)));
            using var inner = Geo.TopRim(p, 2.5); s.Path(inner, "#000000", 0.12);
            s.StrokePath(p, sf.FieldBorder, 1.2, 0.6);
        }
    }

    private static void Toggle(SvgDoc s, Surface sf, bool on)
    {
        using var track = Geo.RRect(12, 26, 104, 48, 24);
        Track(s, track, sf, on);
        Knob(s, on ? 92 : 36, 50, 19);
    }

    private static void Checkbox(SvgDoc s, Surface sf, int state)
    {
        using var box = Geo.RRect(22, 20, 84, 84, 18);
        if (state == 0)
        {
            s.Shadow(box, 0, 2, 4, 0.18);
            s.Path(box, sf.Field);
            using var inner = Geo.Deflate(box, 4);
            s.Path(inner, s.V(24, 104, new Stop(0, sf.Field), new Stop(1, sf.Key == "light" ? "#F1F3F6" : "#252A31")));
            s.StrokePath(box, sf.FieldBorder, 4);
            return;
        }
        Art.Glossy(s, box, Accent, 0.8);
        if (state == 1)
        {
            using var c = Geo.Open(new List<(double, double)> { (42, 62), (58, 78), (88, 46) });
            using var st = Geo.Stroke(c, 12);
            Art.Ink(s, st, "#FFFFFF", 0.2);
        }
        else
        {
            using var d = Geo.RRect(40, 56, 48, 12, 6);
            Art.Ink(s, d, "#FFFFFF", 0.2);
        }
    }

    private static void Radio(SvgDoc s, Surface sf, bool on)
    {
        using var c = Geo.Circle(64, 62, 44);
        if (!on)
        {
            s.Shadow(c, 0, 2, 4, 0.18);
            s.Path(c, s.V(18, 106, new Stop(0, sf.Field), new Stop(1, sf.Key == "light" ? "#EEF0F3" : "#252A31")));
            s.StrokePath(c, sf.FieldBorder, 4);
            return;
        }
        Art.Glossy(s, c, Accent, 0.8);
        using var dot = Geo.Circle(64, 62, 18);
        Knob(s, 64, 62, 18);
    }

    private static void Button(SvgDoc s, Surface sf, bool primary)
    {
        using var b = Geo.RRect(12, 16, 168, 60, 14);
        if (primary) Art.Glossy(s, b, Accent, 0.7, 0.02);
        else
        {
            s.Shadow(b, 0, 2.5, 5, 0.22);
            s.Path(b, s.V(16, 76, new Stop(0, sf.Key == "light" ? "#FFFFFF" : "#4A515C"), new Stop(1, sf.Key == "light" ? "#EDEFF2" : "#3A4049")));
            s.StrokePath(b, sf.FieldBorder, 1.5, 0.9);
            using var rim = Geo.TopRim(b, 1.5); s.Path(rim, "#FFFFFF", sf.Key == "light" ? 0.9 : 0.15);
        }
        using var t = Txt.Fit(primary ? "OK" : "Cancel", "fira-semibold", 28, 12, 16, 168, 60);
        Art.Ink(s, t, primary ? "#FFFFFF" : sf.Text, primary ? 0.22 : 0);
    }

    private static void Field(SvgDoc s, Surface sf, int kind)
    {
        using var f = Geo.RRect(10, 12, 204, 54, 12);
        s.Shadow(f, 0, 2, 4, 0.16);
        s.Path(f, sf.Field);
        using var inset = Geo.TopRim(f, 3); s.Path(inset, "#000000", 0.08);
        s.StrokePath(f, kind == 0 ? Accent.Mid : sf.FieldBorder, kind == 0 ? 2.5 : 1.5);
        if (kind == 0)
        {
            using var bottom = Geo.RRect(16, 62, 192, 4, 2); s.Path(bottom, Accent.Mid);
            using var text = Geo.RRect(28, 32, 96, 14, 7); s.Path(text, sf.Text, 0.75);
            using var caret = Geo.Rect(130, 26, 3, 26); s.Path(caret, Accent.Mid);
        }
        else if (kind == 1)
        {
            using var mag = Magnifier(26, 24, 30);
            s.Path(mag, sf.Muted);
            using var ph = Geo.RRect(66, 33, 100, 12, 6); s.Path(ph, sf.Muted, 0.7);
        }
        else
        {
            using var ph = Geo.RRect(28, 33, 110, 12, 6); s.Path(ph, sf.Text, 0.7);
            using var chev = Geo.Open(new List<(double, double)> { (178, 33), (188, 44), (198, 33) });
            using var st = Geo.Stroke(chev, 4.5); s.Path(st, sf.Text);
        }
    }

    private static SKPath Magnifier(double x, double y, double size)
    {
        var k = size / 24;
        var p = new SKPath();
        p.AddCircle((float)(x + 10 * k), (float)(y + 10 * k), (float)(6.5 * k));
        p.MoveTo((float)(x + 15 * k), (float)(y + 15 * k)); p.LineTo((float)(x + 21 * k), (float)(y + 21 * k));
        var r = Geo.Stroke(p, 2.6 * k);
        p.Dispose();
        return r;
    }

    private static void Slider(SvgDoc s, Surface sf)
    {
        using var track = Geo.RRect(14, 26, 196, 12, 6);
        Track(s, track, sf, false);
        using var fill = Geo.RRect(14, 26, 120, 12, 6);
        s.Path(fill, s.V(26, 38, new Stop(0, Accent.Hi), new Stop(1, Accent.Lo)));
        Knob(s, 134, 32, 17);
        using var dot = Geo.Circle(134, 32, 7); s.Path(dot, Accent.Mid);
    }

    private static void Progress(SvgDoc s, Surface sf)
    {
        using var track = Geo.RRect(10, 12, 204, 20, 10);
        Track(s, track, sf, false);
        using var fill = Geo.RRect(10, 12, 140, 20, 10);
        s.Path(fill, s.Linear(10, 0, 150, 0, new Stop(0, Accent.Lo), new Stop(1, Accent.Hi)));
        var stripes = new SKPath();
        for (var x = -10.0; x < 160; x += 18) stripes.AddPoly(new[] { new SKPoint((float)x, 32), new SKPoint((float)(x + 9), 32), new SKPoint((float)(x + 29), 12), new SKPoint((float)(x + 20), 12) });
        using (var st = Geo.Inter(fill, stripes)) s.Path(st, "#FFFFFF", 0.18);
        stripes.Dispose();
        using var rim = Geo.TopRim(fill, 2); s.Path(rim, "#FFFFFF", 0.45);
    }

    private static void Spinner(SvgDoc s, Surface sf)
    {
        for (var i = 0; i < 10; i++)
        {
            var a = (-90 + i * 36) * Math.PI / 180;
            var r = 6 + i * 0.9;
            using var d = Geo.Circle(64 + Math.Cos(a) * 42, 64 + Math.Sin(a) * 42, r);
            s.Path(d, s.V(64 + Math.Sin(a) * 42 - r, 64 + Math.Sin(a) * 42 + r, new Stop(0, Accent.Hi), new Stop(1, Accent.Lo)), 0.25 + i * 0.075);
        }
    }

    private static void Caption(SvgDoc s, Surface sf, int kind)
    {
        using var b = Geo.RRect(18, 18, 92, 92, 18);
        if (kind == 0) Art.Glossy(s, b, Pal.Red, 0.7);
        else Art.Glossy(s, b, sf.Body, 0.7);
        var ink = kind == 0 ? "#FFFFFF" : sf.Body.On;
        SKPath g;
        if (kind == 0)
        {
            var p = new SKPath(); p.MoveTo(46, 46); p.LineTo(82, 82); p.MoveTo(82, 46); p.LineTo(46, 82);
            g = Geo.Stroke(p, 7); p.Dispose();
        }
        else if (kind == 1) g = Geo.RRect(42, 62, 44, 7, 3.5);
        else { using var sq = Geo.RRect(44, 44, 40, 40, 6); g = Geo.Stroke(sq, 6); }
        Art.Ink(s, g, ink, kind == 0 ? 0.2 : 0);
        g.Dispose();
    }

    private static void WindowControls(SvgDoc s, Surface sf)
    {
        using var bar = Geo.RRect(6, 10, 212, 56, 12);
        s.Shadow(bar, 0, 2, 4, 0.2);
        s.Path(bar, s.V(10, 66, new Stop(0, sf.Body.Hi), new Stop(1, sf.Body.Lo)));
        s.StrokePath(bar, sf.FieldBorder, 1.2, 0.6);
        using var close = Geo.RRect(150, 10, 68, 56, 0, 12, 12, 0);
        s.Path(close, s.V(10, 66, new Stop(0, Pal.Red.Hi), new Stop(1, Pal.Red.Lo)));
        var ink = sf.Body.On;
        using (var mn = Geo.Rect(26, 37, 26, 3.5)) s.Path(mn, ink);
        using (var sq = Geo.RRect(94, 26, 24, 24, 3)) { using var st = Geo.Stroke(sq, 3.5); s.Path(st, ink); }
        var p = new SKPath(); p.MoveTo(172, 26); p.LineTo(196, 50); p.MoveTo(196, 26); p.LineTo(172, 50);
        using (var x = Geo.Stroke(p, 3.5)) s.Path(x, "#FFFFFF");
        p.Dispose();
    }

    private static void IconButton(SvgDoc s, Surface sf, int kind)
    {
        using var b = Geo.RRect(18, 18, 92, 92, 22);
        Art.Glossy(s, b, sf.Body, 0.7);
        var ink = sf.Body.On;
        if (kind == 0)
            for (var i = 0; i < 3; i++) { using var r = Geo.RRect(40, 44 + i * 18, 48, 8, 4); Art.Ink(s, r, ink, 0); }
        else
            for (var i = 0; i < 3; i++)
            {
                using var d = kind == 1 ? Geo.Circle(64, 44 + i * 20, 6.5) : Geo.Circle(44 + i * 20, 64, 6.5);
                Art.Ink(s, d, ink, 0);
            }
    }

    private static void Bell(SvgDoc s, Surface sf)
    {
        var p = new SKPath();
        p.MoveTo(64, 20); p.CubicTo(44, 20, 34, 36, 34, 56); p.LineTo(34, 74); p.LineTo(24, 90); p.LineTo(104, 90); p.LineTo(94, 74); p.LineTo(94, 56); p.CubicTo(94, 36, 84, 20, 64, 20); p.Close();
        using var knob = Geo.Circle(64, 18, 6);
        using var clap = Geo.Circle(64, 98, 10);
        using var u1 = Geo.Union(p, knob);
        using var bell = Geo.Union(u1, clap);
        p.Dispose();
        Art.Glossy(s, bell, sf.Key == "light" ? Pal.Gold : Pal.Gold, 1);
        using var badge = Geo.Circle(96, 32, 20);
        using var ring = Geo.Circle(96, 32, 24);
        s.Path(ring, sf.Key == "light" ? "#FFFFFF" : "#1E2025");
        Art.Glossy(s, badge, Pal.Red, 0.5);
        using var t = Txt.Fit("3", "fira-bold", 28, 82, 18, 28, 28);
        s.Path(t, "#FFFFFF");
    }

    // ------------------------------------------------------------------ cursors

    private static void CursorShape(SvgDoc s, SKPath body, bool black = false)
    {
        using var outline = Geo.Inflate(body, 3.6);
        s.Shadow(outline, 2, 4, 6, 0.35);
        s.Path(outline, black ? "#FFFFFF" : "#15181D");
        var b = body.TightBounds;
        s.Path(body, black ? s.V(b.Top, b.Bottom, new Stop(0, "#3A4049"), new Stop(1, "#0E1013")) : s.V(b.Top, b.Bottom, new Stop(0, "#FFFFFF"), new Stop(1, "#E4E7EC")));
        using var rim = Geo.TopRim(body, 1.6); s.Path(rim, "#FFFFFF", black ? 0.25 : 0.9);
    }

    private static SKPath ArrowCursor(double x = 34, double y = 12, double k = 1)
    {
        var pts = new List<(double, double)> { (0, 0), (0, 86), (21, 66), (35, 98), (50, 91), (36, 60), (64, 60) }.Select(p => (x + p.Item1 * k, y + p.Item2 * k)).ToList();
        return Geo.RoundPoly(pts, 2.5);
    }

    private static SKPath HandCursor()
    {
        var parts = new List<SKPath>
        {
            Geo.RRect(46, 8, 20, 70, 10),
            Geo.RRect(64, 40, 18, 44, 9), Geo.RRect(80, 46, 17, 40, 8.5), Geo.RRect(95, 54, 15, 34, 7.5),
            Geo.RRect(44, 60, 66, 58, 20),
            Geo.Rotate(Geo.RRect(22, 64, 18, 44, 9), -38, 40, 96),
        };
        var u = Geo.UnionAll(parts);
        foreach (var p in parts) p.Dispose();
        return u;
    }

    private static SKPath DoubleArrow(double x0, double y0, double x1, double y1) => Art.Arrow(new List<(double X, double Y)> { (x0, y0), (x1, y1) }, 9, 24, 30, startHead: true);

    private static void Cursors(Pack pack)
    {
        L cursor = new("cursor", "Mauszeiger", "cursor", "curseur", "カーソル", "cursor");
        L pointer = new("pointer", "Zeiger", "puntero", "pointeur", "ポインター", "ponteiro");
        var list = new (string Key, L Name, Action<SvgDoc> Draw)[]
        {
            ("cursor-arrow", new("Arrow cursor", "Pfeilzeiger", "Cursor de flecha", "Curseur flèche", "矢印カーソル", "Cursor de seta"), s => { using var a = ArrowCursor(); CursorShape(s, a); }),
            ("cursor-arrow-black", new("Black arrow cursor", "Schwarzer Pfeilzeiger", "Cursor de flecha negro", "Curseur flèche noir", "黒い矢印カーソル", "Cursor de seta preto"), s => { using var a = ArrowCursor(); CursorShape(s, a, true); }),
            ("cursor-hand", new("Hand cursor", "Handzeiger", "Cursor de mano", "Curseur main", "手のカーソル", "Cursor de mão"), s => HandDraw(s)),
            ("cursor-text", new("Text cursor", "Textcursor", "Cursor de texto", "Curseur texte", "テキストカーソル", "Cursor de texto"), s =>
            {
                var p = new SKPath();
                p.MoveTo(46, 16); p.QuadTo(64, 14, 64, 28); p.QuadTo(64, 14, 82, 16);
                p.MoveTo(64, 28); p.LineTo(64, 100);
                p.MoveTo(46, 112); p.QuadTo(64, 114, 64, 100); p.QuadTo(64, 114, 82, 112);
                p.MoveTo(54, 64); p.LineTo(74, 64);
                using var st = Geo.Stroke(p, 5.5); p.Dispose();
                CursorShape(s, st);
            }),
            ("cursor-busy", new("Busy cursor", "Beschäftigt-Zeiger", "Cursor ocupado", "Curseur occupé", "待機カーソル", "Cursor ocupado"), s =>
            {
                using var o = Geo.Circle(64, 64, 44); using var i = Geo.Circle(64, 64, 28);
                using var ring = Geo.Diff(o, i);
                using var outline = Geo.Inflate(ring, 3);
                s.Shadow(outline, 2, 4, 6, 0.3);
                s.Path(outline, "#15181D");
                s.Path(ring, "#E4E7EC");
                using var arc = Geo.Open(Art.Arc(64, 64, 36, 36, -90, 150));
                using var st = Geo.Stroke(arc, 14, SKStrokeCap.Butt);
                s.Path(st, s.Linear(64, 28, 40, 90, new Stop(0, Accent.Hi), new Stop(1, Accent.Lo)));
            }),
            ("cursor-crosshair", new("Crosshair cursor", "Fadenkreuz", "Cursor en cruz", "Curseur réticule", "十字カーソル", "Cursor em cruz"), s =>
            {
                var p = new SKPath();
                p.MoveTo(64, 12); p.LineTo(64, 52); p.MoveTo(64, 76); p.LineTo(64, 116); p.MoveTo(12, 64); p.LineTo(52, 64); p.MoveTo(76, 64); p.LineTo(116, 64);
                using var st = Geo.Stroke(p, 5, SKStrokeCap.Butt); p.Dispose();
                using var dot = Geo.Circle(64, 64, 4);
                using var u = Geo.Union(st, dot);
                CursorShape(s, u);
            }),
            ("cursor-move", new("Move cursor", "Verschieben-Zeiger", "Cursor mover", "Curseur déplacer", "移動カーソル", "Cursor mover"), s =>
            {
                using var a = DoubleArrow(10, 64, 118, 64); using var b = DoubleArrow(64, 10, 64, 118);
                using var u = Geo.Union(a, b); CursorShape(s, u);
            }),
            ("cursor-resize-horizontal", new("Horizontal resize cursor", "Zeiger horizontal skalieren", "Cursor de cambio de tamaño horizontal", "Curseur redimensionner horizontal", "左右サイズ変更カーソル", "Cursor redimensionar horizontal"), s => { using var a = DoubleArrow(10, 64, 118, 64); CursorShape(s, a); }),
            ("cursor-resize-vertical", new("Vertical resize cursor", "Zeiger vertikal skalieren", "Cursor de cambio de tamaño vertical", "Curseur redimensionner vertical", "上下サイズ変更カーソル", "Cursor redimensionar vertical"), s => { using var a = DoubleArrow(64, 10, 64, 118); CursorShape(s, a); }),
            ("cursor-resize-diagonal", new("Diagonal resize cursor", "Zeiger diagonal skalieren", "Cursor de cambio de tamaño diagonal", "Curseur redimensionner diagonal", "斜めサイズ変更カーソル", "Cursor redimensionar diagonal"), s => { using var a = DoubleArrow(20, 20, 108, 108); CursorShape(s, a); }),
            ("cursor-resize-diagonal-2", new("Diagonal resize cursor 2", "Zeiger diagonal skalieren 2", "Cursor de cambio de tamaño diagonal 2", "Curseur redimensionner diagonal 2", "斜めサイズ変更カーソル 2", "Cursor redimensionar diagonal 2"), s => { using var a = DoubleArrow(108, 20, 20, 108); CursorShape(s, a); }),
            ("cursor-not-allowed", new("Not-allowed cursor", "Nicht-erlaubt-Zeiger", "Cursor no permitido", "Curseur interdit", "禁止カーソル", "Cursor não permitido"), s =>
            {
                using var o = Geo.Circle(64, 64, 46); using var i = Geo.Circle(64, 64, 33);
                using var ring = Geo.Diff(o, i);
                using var bar = Geo.Rotate(Geo.Rect(22, 57, 84, 14), -45, 64, 64);
                using var u = Geo.Union(ring, bar);
                using var outline = Geo.Inflate(u, 3);
                s.Shadow(outline, 2, 4, 6, 0.3);
                s.Path(outline, "#FFFFFF");
                Art.Glossy(s, u, Pal.Red, 0, 0.04, 0.6);
            }),
            ("cursor-help", new("Help cursor", "Hilfe-Zeiger", "Cursor de ayuda", "Curseur aide", "ヘルプカーソル", "Cursor de ajuda"), s =>
            {
                using var a = ArrowCursor(16, 12, 0.85); CursorShape(s, a);
                using var c = Geo.Circle(94, 82, 24);
                Art.Glossy(s, c, Pal.Blue, 0.6);
                using var q = Txt.Fit("?", "fira-bold", 34, 82, 68, 24, 28, tight: true);
                s.Path(q, "#FFFFFF");
            }),
            ("cursor-zoom-in", new("Zoom-in cursor", "Vergrößern-Zeiger", "Cursor ampliar", "Curseur zoom avant", "拡大カーソル", "Cursor ampliar"), s => Zoom(s, true)),
            ("cursor-zoom-out", new("Zoom-out cursor", "Verkleinern-Zeiger", "Cursor reducir", "Curseur zoom arrière", "縮小カーソル", "Cursor reduzir"), s => Zoom(s, false)),
            ("cursor-arrow-click", new("Arrow cursor with click", "Pfeilzeiger mit Klick", "Cursor de flecha con clic", "Curseur flèche avec clic", "クリックする矢印カーソル", "Cursor de seta com clique"), s =>
            {
                ClickHalo(s, 40, 22);
                using var a = ArrowCursor(40, 22, 0.95); CursorShape(s, a);
            }),
            ("cursor-hand-click", new("Hand cursor with click", "Handzeiger mit Klick", "Cursor de mano con clic", "Curseur main avec clic", "クリックする手のカーソル", "Cursor de mão com clique"), s =>
            {
                ClickHalo(s, 56, 14);
                HandDraw(s);
            }),
        };
        foreach (var (key, name, draw) in list)
        {
            var s = new SvgDoc(128, 128);
            draw(s);
            pack.Add(key, s.ToString(), name, cursor, pointer);
        }
    }

    private static void HandDraw(SvgDoc s)
    {
        using var h = HandCursor();
        CursorShape(s, h);
        foreach (var x in new[] { 65.5, 81.5, 96 })
        {
            using var l = Geo.Open(new List<(double, double)> { (x, 70), (x, 82) });
            s.StrokePath(l, "#9AA1AD", 2.2);
        }
    }

    private static void ClickHalo(SvgDoc s, double x, double y)
    {
        s.Path(Geo.Circle(x, y, 26), s.Radial(x, y, 26, new[] { new Stop(0, "#FFD43B", 0.85), new Stop(0.6, "#FFD43B", 0.45), new Stop(1, "#FFD43B", 0) }));
        foreach (var a in new[] { 200.0, 240, 280, 320 })
        {
            var r = a * Math.PI / 180;
            using var l = Geo.Open(new List<(double, double)> { (x + Math.Cos(r) * 14, y + Math.Sin(r) * 14), (x + Math.Cos(r) * 24, y + Math.Sin(r) * 24) });
            using var st = Geo.Stroke(l, 4);
            s.Path(st, Pal.Orange.Mid);
        }
    }

    private static void Zoom(SvgDoc s, bool plus)
    {
        using var o = Geo.Circle(54, 54, 38); using var i = Geo.Circle(54, 54, 28);
        using var ring = Geo.Diff(o, i);
        using var handle = Geo.Rotate(Geo.RRect(78, 47, 44, 16, 8), 45, 82, 55);
        using var body = Geo.Union(ring, handle);
        CursorShape(s, body);
        using var lens = Geo.Circle(54, 54, 28);
        s.Path(lens, s.Radial(46, 44, 34, new[] { new Stop(0, "#FFFFFF", 0.9), new Stop(1, "#D0E4FF", 0.6) }));
        using var h = Geo.RRect(38, 50, 32, 8, 4);
        SKPath sign = plus ? Geo.Union(h, Geo.RRect(50, 38, 8, 32, 4)) : new SKPath(h);
        s.Path(sign, Accent.Lo);
        sign.Dispose();
    }
}
