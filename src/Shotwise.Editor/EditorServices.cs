using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Settings;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;
using Shotwise.Editor.Tools;

namespace Shotwise.Editor;

/// <summary>
/// Engine services an <see cref="EditorSession"/> uses. Renderer, effect processor and codec are required; the rest are
/// optional — features depending on a missing service report <see cref="EditorMessageKind.FeatureUnavailable"/>.
/// Unit tests pass fakes.
/// </summary>
public sealed record EditorServices(IDocumentRenderer Renderer, IEffectProcessor Effects, IImageCodec Codec)
{
    public ISmartMoveService? SmartMove { get; init; }
    public IInpaintingService? Inpainting { get; init; }
    public IMagicWandService? MagicWand { get; init; }
    public ISimplifyService? Simplify { get; init; }
    public ISmartRedactService? SmartRedact { get; init; }
    public IOcrService? Ocr { get; init; }
    public ITextEditService? TextEdit { get; init; }
    public IStyleRepository? Styles { get; init; }
    public ISettingsService? Settings { get; init; }
    /// <summary>Tool factory; null = <see cref="DefaultToolFactory"/>.</summary>
    public IToolFactory? Tools { get; init; }
    public ILoggerFactory? LoggerFactory { get; init; }
    /// <summary>Shared "last style per tool" memory; null = per-session only.</summary>
    public IToolStyleMemory? StyleMemory { get; init; }

    /// <summary>Builds the record from a DI container (required services must be registered).</summary>
    public static EditorServices FromServiceProvider(IServiceProvider sp) =>
        new(sp.GetRequiredService<IDocumentRenderer>(), sp.GetRequiredService<IEffectProcessor>(), sp.GetRequiredService<IImageCodec>())
        {
            SmartMove = sp.GetService<ISmartMoveService>(),
            Inpainting = sp.GetService<IInpaintingService>(),
            MagicWand = sp.GetService<IMagicWandService>(),
            Simplify = sp.GetService<ISimplifyService>(),
            SmartRedact = sp.GetService<ISmartRedactService>(),
            Ocr = sp.GetService<IOcrService>(),
            TextEdit = sp.GetService<ITextEditService>(),
            Styles = sp.GetService<IStyleRepository>(),
            Settings = sp.GetService<ISettingsService>(),
            Tools = sp.GetService<IToolFactory>(),
            LoggerFactory = sp.GetService<ILoggerFactory>(),
            StyleMemory = sp.GetService<IToolStyleMemory>(),
        };
}

/// <summary>Redraw request; <see cref="DirtyRect"/> null = whole canvas.</summary>
public sealed class InvalidatedEventArgs : EventArgs
{
    public static new InvalidatedEventArgs Empty { get; } = new(null);
    public InvalidatedEventArgs(Rect2? dirtyRect) => DirtyRect = dirtyRect;
    public Rect2? DirtyRect { get; }
}

/// <summary>Asks the view to open the inline rich-text editor over an annotation.</summary>
/// <param name="AnnotationId">Text or callout annotation being edited.</param>
/// <param name="Bounds">Text box in canvas pixels (unrotated).</param>
/// <param name="RotationDegrees">Rotation of the box around its centre.</param>
/// <param name="Text">Current rich text.</param>
/// <param name="Style">Box layout style (alignment, padding, outline).</param>
/// <param name="IsNew">True when the annotation was just created (empty box may be discarded on commit).</param>
/// <param name="CaretIndex">Initial caret position (null = end).</param>
public sealed record TextEditRequest(Guid AnnotationId, Rect2 Bounds, double RotationDegrees, RichText Text, TextLayoutStyle Style, bool IsNew, int? CaretIndex = null);

public enum EditorMessageKind { Info, Warning, Error, FeatureUnavailable }

/// <summary>User-facing notification from the session (e.g. Smart Move found no objects). UI localizes by <see cref="Code"/>.</summary>
public sealed record EditorMessage(EditorMessageKind Kind, ErrorCode Code, string Message);
