using System.Windows;
using System.Windows.Markup;

// Default styles of the Shotwise custom controls live in Themes/Generic.xaml (fallback; apps merge Themes/Shotwise.xaml).
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

// One XAML namespace for the design system: xmlns:sw="urn:shotwise-ui"
[assembly: XmlnsDefinition("urn:shotwise-ui", "Shotwise.UI.Common.Controls")]
[assembly: XmlnsDefinition("urn:shotwise-ui", "Shotwise.UI.Common.Theming")]
[assembly: XmlnsPrefix("urn:shotwise-ui", "sw")]
