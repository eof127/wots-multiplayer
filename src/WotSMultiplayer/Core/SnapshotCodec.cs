using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace WotSMultiplayer.Core
{
    /// <summary>
    /// Campaign snapshot helpers (pure .NET, unit-tested):
    ///  - stable mobile IDs ("mpId") injected into each mobile's JSON so host and client can match task forces/air groups
    ///    (vanilla has no persistent IDs; list indexes shift whenever a mobile is created or destroyed);
    ///  - removal of UnityEngine.Object references ({"instanceID":N}) before FromJsonOverwrite on another machine,
    ///    because instance IDs are only meaningful inside the process that wrote them;
    ///  - gzip with an uncompressed fallback (flag byte).
    /// </summary>
    internal static class SnapshotCodec
    {
        public const string IdKey = "mpId";
        public static readonly string[] MobileLists = { "playerMobileObjectSaveData", "enemyMobileObjectSaveData" };

        public static string InjectIds(string saveJson, IList<int> playerIds, IList<int> enemyIds)
        {
            JObject root = MiniJson.ParseObject(saveJson);
            Inject(root.Get(MobileLists[0]) as JArray, playerIds);
            Inject(root.Get(MobileLists[1]) as JArray, enemyIds);
            return MiniJson.Write(root);
        }

        private static void Inject(JArray arr, IList<int> ids)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Items.Count && i < ids.Count; i++)
            {
                JObject m = MiniJson.ParseObject(((JString)arr.Items[i]).Value);
                m.Set(IdKey, JRaw.Int(ids[i]));
                arr.Items[i] = new JString(MiniJson.Write(m));
            }
        }

        /// <summary>Removes members whose value is a Unity object reference ({"instanceID":N}); recursive.</summary>
        public static void StripObjectRefs(JObject o)
        {
            for (int i = o.Members.Count - 1; i >= 0; i--)
            {
                JNode v = o.Members[i].Value;
                JObject child = v as JObject;
                if (child != null)
                {
                    if (child.Members.Count == 1 && child.Members[0].Key == "instanceID") { o.Members.RemoveAt(i); continue; }
                    StripObjectRefs(child);
                }
                JArray arr = v as JArray;
                if (arr != null)
                    foreach (JNode item in arr.Items) { JObject io = item as JObject; if (io != null) StripObjectRefs(io); }
            }
        }

        public static int GetId(JObject mobile)
        {
            JRaw r = mobile.Get(IdKey) as JRaw;
            return r != null ? r.AsInt() : -1;
        }

        // ---------- compression ----------

        public static byte[] Pack(string json, bool tryCompress = true)
        {
            byte[] raw = Encoding.UTF8.GetBytes(json);
            if (tryCompress)
            {
                try
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        ms.WriteByte(1);
                        using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                        return ms.ToArray();
                    }
                }
                catch { /* fall through: no zlib available */ }
            }
            byte[] plain = new byte[raw.Length + 1];
            plain[0] = 0;
            Buffer.BlockCopy(raw, 0, plain, 1, raw.Length);
            return plain;
        }

        /// <summary>Most bytes a packed message may unpack to (real campaign states are a few hundred KB).</summary>
        public const int MaxUnpackedBytes = 32 * 1024 * 1024;

        public static string Unpack(byte[] data)
        {
            if (data.Length == 0) return "";
            if (data[0] == 0) return Encoding.UTF8.GetString(data, 1, data.Length - 1);
            using (MemoryStream src = new MemoryStream(data, 1, data.Length - 1))
            using (GZipStream gz = new GZipStream(src, CompressionMode.Decompress))
            using (MemoryStream dst = new MemoryStream())
            {
                byte[] buf = new byte[16384];
                int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                {
                    // a tiny message that unpacks to gigabytes ("zip bomb") is refused instead of filling the memory
                    if (dst.Length + n > MaxUnpackedBytes) throw new ArgumentException("packed message unpacks to more than " + MaxUnpackedBytes / (1024 * 1024) + " MB");
                    dst.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(dst.ToArray());
            }
        }
    }
}
