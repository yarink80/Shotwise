using Windows.Networking.Connectivity;

namespace Shotwise.Platform;

/// <summary>Connection state used by the updater (offline handling and the metered-connection download gate).</summary>
public interface INetworkStatus
{
    /// <summary>False when Windows reports no internet connection profile at all.</summary>
    bool IsOnline { get; }
    /// <summary>True on a metered (fixed/variable cost), roaming or over-limit connection; also true when the cost cannot be read.</summary>
    bool IsMetered { get; }
}

/// <summary>WinRT <see cref="NetworkInformation"/> view of the current internet connection profile.</summary>
public sealed class NetworkStatus : INetworkStatus
{
    public bool IsOnline
    {
        get
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                return profile is not null && profile.GetNetworkConnectivityLevel() is NetworkConnectivityLevel.InternetAccess or NetworkConnectivityLevel.ConstrainedInternetAccess;
            }
            catch (Exception) { return true; }   // unknown: let the request itself decide
        }
    }

    public bool IsMetered
    {
        get
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile is null) return true;
                var cost = profile.GetConnectionCost();
                // Unknown cost (common for Ethernet/VPN adapters) behaves as unrestricted, per Windows' connection-cost guidance.
                return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming || cost.OverDataLimit;
            }
            catch (Exception) { return true; }   // cannot tell: never download in the background
        }
    }
}
