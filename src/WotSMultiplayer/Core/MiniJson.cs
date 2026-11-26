using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WotSMultiplayer.Core
{
    // Minimal order-preserving JSON DOM for rewriting Unity JsonUtility save data.
    // Numbers/true/false/null are kept as raw text so a parse → write round trip reproduces JsonUtility's output
    // exactly (no float re-formatting). No Unity references (unit-tested against real saves).

    internal abstract class JNode { }

    internal sealed class JRaw : JNode
    {
        public string Text;
        public JRaw(string text) { Text = text; }
        public int AsInt() => (int)double.Parse(Text, CultureInfo.InvariantCulture);
        public static JRaw Int(int v) => new JRaw(v.ToString(CultureInfo.InvariantCulture));
    }

    internal sealed class JString : JNode
    {
        public string Value;
        public JString(string v) { Value = v; }
    }

    internal sealed class JArray : JNode
    {
        public readonly List<JNode> Items = new List<JNode>();
    }

    internal sealed class JObject : JNode
    {
        public readonly List<KeyValuePair<string, JNode>> Members = new List<KeyValuePair<string, JNode>>();

        public JNode Get(string key)
        {
            foreach (var kv in Members) if (kv.Key == key) return kv.Value;
            return null;
        }

        public bool Has(string key) => Get(key) != null;

        public void Set(string key, JNode value)
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].Key == key) { Members[i] = new KeyValuePair<string, JNode>(key, value); return; }
            Members.Add(new KeyValuePair<string, JNode>(key, value));
        }
    }

    internal static class MiniJson
    {
        /// <summary>
        /// Deepest nesting accepted. The game's JSON is a few levels deep; a message nested thousands deep would otherwise
        /// overflow the stack, which ends the game process (StackOverflowException can't be caught).
        /// </summary>
        public const int MaxDepth = 128;

        public static JNode Parse(string text)
        {
            int i = 0;
            JNode n = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters at " + i);
            return n;
        }

        public static JObject ParseObject(string text) =>
            Parse(text) as JObject ?? throw new FormatException("JSON is not an object");

        public static string Write(JNode node)
        {
            StringBuilder sb = new StringBuilder();
            WriteNode(sb, node);
            return sb.ToString();
        }

        // ---------- parser ----------

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static JNode ParseValue(string s, ref int i, int depth)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if ((c == '{' || c == '[') && depth >= MaxDepth) throw new FormatException("JSON nested deeper than " + MaxDepth);
            if (c == '{') return ParseObj(s, ref i, depth + 1);
            if (c == '[') return ParseArr(s, ref i, depth + 1);
            if (c == '"') return new JString(ParseStr(s, ref i));
            int start = i;
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            if (i == start) throw new FormatException("bad value at " + start);
            return new JRaw(s.Substring(start, i - start));
        }

        private static JObject ParseObj(string s, ref int i, int depth)
        {
            JObject o = new JObject();
            i++; // {
            SkipWs(s, ref i);
            if (s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseStr(s, ref i);
                SkipWs(s, ref i);
                if (s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                o.Members.Add(new KeyValuePair<string, JNode>(key, ParseValue(s, ref i, depth)));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static JArray ParseArr(string s, ref int i, int depth)
        {
            JArray a = new JArray();
            i++; // [
            SkipWs(s, ref i);
            if (s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Items.Add(ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseStr(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string at " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: throw new FormatException("bad escape at " + i);
                }
            }
        }

        // ---------- writer (matches JsonUtility's compact style) ----------

        private static void WriteNode(StringBuilder sb, JNode n)
        {
            JRaw raw = n as JRaw;
            if (raw != null) { sb.Append(raw.Text); return; }
            JString str = n as JString;
            if (str != null) { WriteStr(sb, str.Value); return; }
            JArray arr = n as JArray;
            if (arr != null)
            {
                sb.Append('[');
                for (int i = 0; i < arr.Items.Count; i++) { if (i > 0) sb.Append(','); WriteNode(sb, arr.Items[i]); }
                sb.Append(']');
                return;
            }
            JObject obj = (JObject)n;
            sb.Append('{');
            for (int i = 0; i < obj.Members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteStr(sb, obj.Members[i].Key);
                sb.Append(':');
                WriteNode(sb, obj.Members[i].Value);
            }
            sb.Append('}');
        }

        private static void WriteStr(StringBuilder sb, string v)
        {
            sb.Append('"');
            foreach (char c in v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
