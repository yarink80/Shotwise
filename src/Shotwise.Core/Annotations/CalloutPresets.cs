namespace Shotwise.Core.Annotations;

/// <summary>
/// Canonical callout shape preset ids (<see cref="CalloutAnnotation.ShapePresetId"/>). WP-RENDERING implements the
/// geometry for each id (our own SVG-path designs); UI galleries list <see cref="All"/>. Unknown ids render as
/// <see cref="RoundedRectangle"/>.
/// </summary>
public static class CalloutPresets
{
    public const string Rectangle = "rectangle";
    public const string RoundedRectangle = "rounded-rectangle";
    public const string Ellipse = "ellipse";
    public const string SpeechBubble = "speech-bubble";
    public const string SpeechBubbleRound = "speech-bubble-round";
    public const string ThoughtBubble = "thought-bubble";
    public const string Cloud = "cloud";
    public const string Banner = "banner";
    public const string Ribbon = "ribbon";
    public const string Tag = "tag";
    public const string Burst = "burst";
    public const string Hexagon = "hexagon";
    public const string Pill = "pill";
    public const string ArrowRight = "arrow-right";
    public const string ArrowLeft = "arrow-left";
    public const string ArrowUp = "arrow-up";
    public const string ArrowDown = "arrow-down";
    public const string ArrowDouble = "arrow-double";
    public const string Note = "note";
    public const string Shield = "shield";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Rectangle, RoundedRectangle, Ellipse, Pill, SpeechBubble, SpeechBubbleRound, ThoughtBubble, Cloud, Banner, Ribbon,
        Tag, Burst, Hexagon, Note, Shield, ArrowRight, ArrowLeft, ArrowUp, ArrowDown, ArrowDouble,
    };

    /// <summary>Presets whose geometry supports user tails.</summary>
    public static bool AllowsTails(string id) => id is Rectangle or RoundedRectangle or Ellipse or Pill or SpeechBubble or SpeechBubbleRound or ThoughtBubble or Cloud or Note;
}
