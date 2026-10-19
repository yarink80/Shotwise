using Windows.Win32;
using Windows.Win32.Devices.Display;

namespace Shotwise.Capture;

internal static class DisplayWhiteLevel
{
    internal static unsafe IReadOnlyDictionary<string, double> Read()
    {
        var levels = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int retry = 0; retry < 3; retry++)
        {
            if ((int)PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) break;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount]; var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            fixed (DISPLAYCONFIG_PATH_INFO* pp = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* pm = modes)
            {
                var error = PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, pp, &modeCount, pm, null);
                if ((int)error == 122) continue;
                if ((int)error != 0) break;
                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];
                    var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME); source.header.adapterId = path.sourceInfo.adapterId; source.header.id = path.sourceInfo.id;
                    if ((int)PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0) continue;
                    var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                    white.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                    white.header.size = (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL); white.header.adapterId = path.targetInfo.adapterId; white.header.id = path.targetInfo.id;
                    if ((int)PInvoke.DisplayConfigGetDeviceInfo(&white.header) == 0) levels[source.viewGdiDeviceName.ToString()] = white.SDRWhiteLevel * 80d / 1000;
                }
            }
            break;
        }
        return levels;
    }
}
