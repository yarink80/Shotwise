namespace Shotwise.Core;

/// <summary>
/// User-presentable failure codes. UI maps each code to a localized message (Strings.resx key "Error_{Code}").
/// Append new values at the end of a group; never renumber (codes may be logged/persisted).
/// </summary>
public enum ErrorCode
{
    Unknown = 0,
    Cancelled = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    OutOfMemory = 4,
    PolicyDisabled = 5,

    // Files & formats (100+)
    FileNotFound = 100,
    AccessDenied = 101,
    DiskFull = 102,
    InvalidFileFormat = 103,
    UnsupportedFormat = 104,
    CorruptFile = 105,
    NewerFormatVersion = 106,
    FileTooLarge = 107,
    PathTraversal = 108,
    TooManyEntries = 109,
    ImageTooLarge = 110,
    CodecUnavailable = 111,
    FileInUse = 112,
    ImportFailed = 113,
    ExportFailed = 114,
    AssetMissing = 115,

    // Capture (200+)
    CaptureFailed = 200,
    CaptureCancelled = 201,
    NoMonitor = 202,
    WindowNotFound = 203,
    SelectionTooSmall = 204,
    ScrollingFailed = 205,
    ScrollingNotScrollable = 206,
    StitchingFailed = 207,
    HdrUnsupported = 208,
    ClipboardEmpty = 209,
    ClipboardUnavailable = 210,
    PrinterInstallFailed = 211,
    PrinterLoopRefused = 212,
    CaptureProtectedContent = 213,

    // Recording & media (300+)
    RecordingFailed = 300,
    DeviceBusy = 301,
    DeviceNotFound = 302,
    PrivacyDenied = 303,
    MediaFeaturePackMissing = 304,
    VideoDisabledByPolicy = 305,
    LowDiskSpace = 306,
    EncoderUnavailable = 307,
    DecoderUnavailable = 308,
    AudioInitFailed = 309,
    CannotCutEntireMedia = 310,
    GifTooLong = 311,
    MediaRenderFailed = 312,

    // Vision (400+)
    OcrUnavailable = 400,
    OcrLanguageMissing = 401,
    OcrFailed = 402,
    OcrImageTooLarge = 403,
    ModelUnavailable = 404,
    ModelDownloadFailed = 405,
    ModelChecksumMismatch = 406,
    NoComponentsFound = 407,
    NoRedactionsFound = 408,
    InpaintFailed = 409,

    // Library (500+)
    LibraryUnavailable = 500,
    LibraryLocationInvalid = 501,
    LibraryMoveFailed = 502,
    BackupFailed = 503,
    RestoreFailed = 504,
    LibraryItemNotFound = 505,

    // Sharing (600+)
    ShareFailed = 600,
    ShareCancelled = 601,
    ShareNoFile = 602,
    ShareMultipleFiles = 603,
    ShareInProgress = 604,
    DestinationNotConfigured = 605,
    DestinationUnavailable = 606,
    AuthenticationFailed = 607,
    AuthorizationRequired = 608,
    NetworkError = 609,
    QuotaExceeded = 610,
    OfficeNotInstalled = 611,
    ProgramNotFound = 612,
    UploadRejected = 613,

    // Platform (700+)
    HotkeyDuplicate = 700,
    HotkeyUnsupported = 701,
    HotkeyInUse = 702,
    UpdateSignatureInvalid = 703,
    UpdateFailed = 704,
    MobileServerFailed = 705,
    MobilePairingExpired = 706,
    SingleInstanceFailed = 707,
    ShellRegistrationFailed = 708,

    // Settings / presets / content (800+)
    SettingsCorrupt = 800,
    PresetImportFailed = 801,
    ThemeImportFailed = 802,
    TemplateImportFailed = 803,
    StampImportFailed = 804,
    TemplateIncompatible = 805,
}

/// <summary>Exception for user-presentable failures; <see cref="Code"/> drives the localized message.</summary>
public class ShotwiseException : Exception
{
    public ErrorCode Code { get; }

    public ShotwiseException(ErrorCode code, string message) : base(message) => Code = code;
    public ShotwiseException(ErrorCode code, string message, Exception? inner) : base(message, inner) => Code = code;

    /// <summary>Optional machine-readable detail (e.g. offending path, hotkey text) for message formatting.</summary>
    public string? Detail { get; init; }

    public override string ToString() => $"[{Code}] {base.ToString()}";
}
