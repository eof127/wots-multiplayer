using UnityEngine;

namespace WotSMultiplayer.Ui
{
    /// <summary>
    /// IMGUI does not scale with resolution; at 1440p+ the default skin is tiny. All mod windows are laid out in a
    /// virtual 1080p-tall space and scaled via GUI.matrix (IMGUI maps mouse input through the matrix automatically).
    /// </summary>
    internal static class UiScale
    {
        internal static float Factor => Mathf.Max(0.5f, Screen.height / 1080f * Plugin.UiScaleSetting.Value);

        /// <summary>Screen size in virtual (pre-scale) units, for positioning.</summary>
        internal static float Width => Screen.width / Factor;
        internal static float Height => Screen.height / Factor;

        internal static void Apply()
        {
            float f = Factor;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(f, f, 1f));
        }
    }
}
