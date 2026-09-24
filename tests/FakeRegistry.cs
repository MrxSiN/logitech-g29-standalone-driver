using System;
using System.Collections.Generic;
using System.Linq;
using G29.Bridge.Runtime;

namespace G29.Tests
{
    // In-memory registry for the registry commands of the ABI. Keys are
    // case-insensitive paths; HKLM paths under SOFTWARE\ have separate 64-bit and
    // 32-bit views (WOW64 redirection), everything else is shared between views.
    internal sealed class FakeRegistry
    {
        private readonly SortedDictionary<string, Dictionary<string, Value>> keys = new SortedDictionary<string, Dictionary<string, Value>>(StringComparer.OrdinalIgnoreCase);

        internal sealed class Value
        {
            internal Value(int kind, object data)
            {
                Kind = kind;
                Data = data;
            }

            internal int Kind { get; private set; }

            internal object Data { get; private set; }

            public override string ToString()
            {
                switch (Kind)
                {
                    case 1:
                        return "sz:" + Data;
                    case 2:
                        return "dword:" + Data;
                    case 3:
                        return "bin:" + ReferenceFixtures.Hex((byte[])Data);
                    default:
                        return "other";
                }
            }
        }

        internal static string Key(int root, int view, string path)
        {
            string hive = root == 1 ? "HKLM" : root == 2 ? "HKCU" : "?";
            bool redirected = root == 1 && view == 2 && path.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase);
            return hive + (redirected ? "32" : string.Empty) + "\\" + path;
        }

        internal void Set(string key, string name, int kind, object data)
        {
            CreateKey(key);
            keys[key][name] = new Value(kind, data);
        }

        internal bool CreateKey(string key)
        {
            bool existed = keys.ContainsKey(key);
            string[] parts = key.Split('\\');
            for (int count = 2; count <= parts.Length; count++)
            {
                string partial = string.Join("\\", parts, 0, count);
                if (!keys.ContainsKey(partial))
                {
                    keys[partial] = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                }
            }

            return existed;
        }

        internal bool Exists(string key)
        {
            return keys.ContainsKey(key);
        }

        internal Value Get(string key, string name)
        {
            Dictionary<string, Value> values;
            Value value;
            return keys.TryGetValue(key, out values) && values.TryGetValue(name, out value) ? value : null;
        }

        internal void DeleteTree(string key)
        {
            foreach (string existing in keys.Keys.ToList())
            {
                if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase) || existing.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    keys.Remove(existing);
                }
            }
        }

        internal int SubKeyCount(string key)
        {
            int depth = key.Split('\\').Length;
            return keys.Keys.Count(existing => existing.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) && existing.Split('\\').Length == depth + 1);
        }

        // Every key and value, one line each, for whole-registry comparisons. Keys
        // that are only the hive roots or empty intermediate keys are included.
        internal List<string> Dump()
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, Value>> key in keys)
            {
                if (key.Key.Split('\\').Length <= 2 && key.Value.Count == 0)
                {
                    continue;
                }

                lines.Add("[" + key.Key + "]");
                foreach (KeyValuePair<string, Value> value in key.Value.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add("  " + (value.Key.Length == 0 ? "@" : value.Key) + " = " + value.Value);
                }
            }

            return lines;
        }

        // Handles a registry command frame and returns the EV_REG_RESULT payload.
        internal byte[] Handle(byte type, PayloadReader reader)
        {
            int root = reader.U8();
            int view = reader.U8();
            string path = reader.Text();
            string key = Key(root, view, path);
            var result = new PayloadWriter();
            switch (type)
            {
                case 0xB0:
                {
                    string name = reader.Text();
                    reader.End();
                    Value value = Get(key, name);
                    if (value == null)
                    {
                        return result.U32(2).U8(0).ToArray();
                    }

                    result.U32(0).U8(value.Kind);
                    if (value.Kind == 1)
                    {
                        result.Text((string)value.Data);
                    }
                    else if (value.Kind == 2)
                    {
                        result.U32((long)value.Data);
                    }

                    return result.ToArray();
                }

                case 0xB1:
                {
                    string name = reader.Text();
                    int kind = reader.U8();
                    object data;
                    if (kind == 1)
                    {
                        data = reader.Text();
                    }
                    else if (kind == 2)
                    {
                        data = reader.U32();
                    }
                    else if (kind == 3)
                    {
                        data = reader.Bytes(reader.U16());
                    }
                    else
                    {
                        throw new InvalidOperationException("Unknown registry value kind " + kind);
                    }

                    reader.End();
                    Set(key, name, kind, data);
                    return result.U32(0).ToArray();
                }

                case 0xB2:
                    reader.End();
                    return result.U32(0).U8(CreateKey(key) ? 1 : 0).ToArray();
                case 0xB3:
                    reader.End();
                    DeleteTree(key);
                    return result.U32(0).ToArray();
                case 0xB4:
                    reader.End();
                    if (!Exists(key))
                    {
                        return result.U32(2).U32(0).U32(0).ToArray();
                    }

                    return result.U32(0).U32(SubKeyCount(key)).U32(keys[key].Count).ToArray();
                case 0xB5:
                    reader.End();
                    if (!Exists(key))
                    {
                        return result.U32(0).ToArray();
                    }

                    if (SubKeyCount(key) > 0 || keys[key].Count > 0)
                    {
                        return result.U32(1018).ToArray();
                    }

                    keys.Remove(key);
                    return result.U32(0).ToArray();
                default:
                    throw new InvalidOperationException("Not a registry command " + type);
            }
        }
    }
}
