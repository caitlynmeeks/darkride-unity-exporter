// A small JSON writer for the entity file: strings, numbers, booleans, vectors, colours, lists and dictionaries, in
// the engine's spellings (position {x, y, z}, rotation {x, y, z, w}, colour {red, green, blue}).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Darkride.Exporter
{
    public static class Json
    {
        public static string Serialize(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent ? 0 : -1);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value, int depth)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: WriteNumber(sb, f); break;
                case double d: WriteNumber(sb, d); break;
                case Vector2 v: WriteObject(sb, depth, ("x", v.x), ("y", v.y)); break;
                case Vector3 v: WriteObject(sb, depth, ("x", v.x), ("y", v.y), ("z", v.z)); break;
                case Vector4 v: WriteObject(sb, depth, ("x", v.x), ("y", v.y), ("z", v.z), ("w", v.w)); break;
                case Quaternion q: WriteObject(sb, depth, ("x", q.x), ("y", q.y), ("z", q.z), ("w", q.w)); break;
                case Color32 c: WriteObject(sb, depth, ("red", (int)c.r), ("green", (int)c.g), ("blue", (int)c.b)); break;
                case Color c: WriteObject(sb, depth, ("red", (int)Mathf.Round(Mathf.Clamp01(c.r) * 255)), ("green", (int)Mathf.Round(Mathf.Clamp01(c.g) * 255)), ("blue", (int)Mathf.Round(Mathf.Clamp01(c.b) * 255))); break;
                case IDictionary<string, object> map: WriteMap(sb, map, depth); break;
                case IEnumerable list: WriteList(sb, list, depth); break;
                default: WriteString(sb, value.ToString()); break;
            }
        }

        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
            // the shortest round-trip form, never an exponent the engine's parser might not expect
            string text = ((float)d).ToString("R", CultureInfo.InvariantCulture);
            if (text.Contains("E")) text = d.ToString("0.##########", CultureInfo.InvariantCulture);
            sb.Append(text);
        }

        static void WriteObject(StringBuilder sb, int depth, params (string, object)[] fields)
        {
            var map = new Dictionary<string, object>();
            foreach (var (k, v) in fields) map[k] = v;
            WriteMap(sb, map, depth);
        }

        static void WriteMap(StringBuilder sb, IDictionary<string, object> map, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in map)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, depth + 1);
                WriteString(sb, kv.Key);
                sb.Append(depth >= 0 ? ": " : ":");
                Write(sb, kv.Value, depth >= 0 ? depth + 1 : -1);
            }
            if (!first) NewLine(sb, depth);
            sb.Append('}');
        }

        static void WriteList(StringBuilder sb, IEnumerable list, int depth)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, depth + 1);
                Write(sb, item, depth >= 0 ? depth + 1 : -1);
            }
            if (!first) NewLine(sb, depth);
            sb.Append(']');
        }

        static void NewLine(StringBuilder sb, int depth)
        {
            if (depth < 0) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
