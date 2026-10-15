// Ported from the user-owned Cabinet engine. Tray, cloud and updater are excluded.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
namespace LuaToolsGui.Services.Cabinet;
class KV
{
    public byte Type;            // 0 subkey, 1 string, 2 int32, 3 float, 7 uint64, 10 int64
    public string Key;
    public object Value;         // List<KV> for subkeys
    public List<KV> Children { get { return Value as List<KV>; } }

    public KV(byte type, string key, object value) { Type = type; Key = key; Value = value; }

    public KV Get(string key)
    {
        var c = Children;
        if (c == null) return null;
        foreach (var k in c) if (string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    public KV GetOrAddSub(string key)
    {
        var k = Get(key);
        if (k == null) { k = new KV(0, key, new List<KV>()); Children.Add(k); }
        return k;
    }

    public void SetInt(string key, int value)
    {
        var k = Get(key);
        if (k == null) Children.Add(new KV(2, key, value));
        else { k.Type = 2; k.Value = value; }
    }

    public void SetFloat(string key, float value)
    {
        var k = Get(key);
        if (k == null) Children.Add(new KV(3, key, value));
        else { k.Type = 3; k.Value = value; }
    }

    public string Str()
    {
        if (Value == null || Value is List<KV>) return null;
        return Convert.ToString(Value, CultureInfo.InvariantCulture);
    }

    public static KV Read(byte[] data)
    {
        if (data.Length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized KV");
        int pos = 0;
        var root = new KV(0, "", new List<KV>());
        ReadInto(data, ref pos, root.Children);
        return root;
    }

    static void ReadInto(byte[] d, ref int pos, List<KV> list, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("KV nesting too deep");
        while (pos < d.Length)
        {
            byte t = d[pos++];
            if (t == 8 || t == 11) return;
            string key = ReadCStr(d, ref pos);
            int size = t is 2 or 3 or 4 or 6 ? 4 : t is 7 or 10 ? 8 : 0;
            if (d.Length - pos < size) throw new InvalidDataException("Truncated KV value");
            object val;
            switch (t)
            {
                case 0: var sub = new List<KV>(); ReadInto(d, ref pos, sub, depth + 1); val = sub; break;
                case 1: val = ReadCStr(d, ref pos); break;
                case 2: case 4: case 6: val = BitConverter.ToInt32(d, pos); pos += 4; break;
                case 3: val = BitConverter.ToSingle(d, pos); pos += 4; break;
                case 7: val = BitConverter.ToUInt64(d, pos); pos += 8; break;
                case 10: val = BitConverter.ToInt64(d, pos); pos += 8; break;
                default: throw new InvalidDataException("Unknown KV type " + t + " at " + pos);
            }
            list.Add(new KV(t, key, val));
        }
        throw new InvalidDataException("Unterminated KV block");
    }

    static string ReadCStr(byte[] d, ref int pos)
    {
        int start = pos;
        while (pos < d.Length && d[pos] != 0) pos++;
        if (pos == d.Length) throw new InvalidDataException("Truncated KV string");
        string s = Encoding.UTF8.GetString(d, start, pos - start);
        pos++;
        return s;
    }

    public byte[] Write()
    {
        var ms = new MemoryStream();
        foreach (var c in Children) WriteNode(ms, c);
        ms.WriteByte(8);
        return ms.ToArray();
    }

    static void WriteNode(Stream s, KV k)
    {
        s.WriteByte(k.Type);
        WriteCStr(s, k.Key);
        byte[] b;
        switch (k.Type)
        {
            case 0: foreach (var c in k.Children) WriteNode(s, c); s.WriteByte(8); return;
            case 1: WriteCStr(s, (string)k.Value); return;
            case 2: case 4: case 6: b = BitConverter.GetBytes((int)k.Value); break;
            case 3: b = BitConverter.GetBytes((float)k.Value); break;
            case 7: b = BitConverter.GetBytes((ulong)k.Value); break;
            case 10: b = BitConverter.GetBytes((long)k.Value); break;
            default: throw new InvalidDataException("Unknown KV type " + k.Type);
        }
        s.Write(b, 0, b.Length);
    }

    static void WriteCStr(Stream s, string str)
    {
        var b = Encoding.UTF8.GetBytes(str);
        s.Write(b, 0, b.Length);
        s.WriteByte(0);
    }
}

// ---------- Sync logic ----------
