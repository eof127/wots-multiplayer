namespace WotSMultiplayer.Core
{
    /// <summary>
    /// Version constants shared by the plugin and the standalone test tools (no Unity references here).
    /// Bump Version on every release; bump Net.Protocol.Version whenever the wire format changes.
    /// </summary>
    internal static class BuildInfo
    {
        public const string Version = "0.28.0";
    }
}
