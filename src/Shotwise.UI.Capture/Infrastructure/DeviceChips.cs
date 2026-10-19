using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Shotwise.Core.Media;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Infrastructure;

/// <summary>Value chip that shows the selected capture device and opens a check-marked device menu.</summary>
internal static class DeviceChips
{
    public static Button Create(IEnumerable<DeviceInfo> devices, string? currentId, string defaultLabel, Action<string?> set, bool includeSystemDefault = true, double maxWidth = 150)
    {
        var list = devices.ToList();
        var effective = currentId ?? (includeSystemDefault ? null : list.FirstOrDefault(d => d.IsDefault)?.Id ?? list.FirstOrDefault()?.Id);
        string NameOf(string? id) => id is null ? defaultLabel : list.FirstOrDefault(d => d.Id == id)?.Name ?? defaultLabel;
        Button chip = null!;
        chip = Ui.ChipButton(NameOf(effective), () =>
        {
            var menu = new ContextMenu { PlacementTarget = chip, Placement = PlacementMode.Bottom };
            void Add(string? id, string header)
            {
                var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = id == effective };
                item.Click += (_, _) =>
                {
                    effective = id;
                    ((TextBlock)chip.Content).Text = NameOf(id);
                    chip.ToolTip = NameOf(id);
                    set(id);
                };
                menu.Items.Add(item);
            }
            if (includeSystemDefault) Add(null, defaultLabel);
            foreach (var d in list) Add(d.Id, d.Name + (d.IsDefault ? " " + CS.Get("Ready_Default") : ""));
            menu.IsOpen = true;
        }, maxWidth: maxWidth);
        return chip;
    }
}
