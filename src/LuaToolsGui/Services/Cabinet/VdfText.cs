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
class VdfText
{
    struct Tok { public char Kind; public int Start, End; public string Text; }   // Kind: 's' string, '{', '}'

    public string Text;
    List<Tok> toks;

    public VdfText(string text) { Text = text; Tokenize(); }

    void Tokenize()
    {
        toks = new List<Tok>();
        var t = Text;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '{' || c == '}') toks.Add(new Tok { Kind = c, Start = i, End = i + 1 });
            else if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                int s = i++;
                while (i < t.Length && t[i] != '"')
                {
                    if (t[i] == '\\' && i + 1 < t.Length) { sb.Append(t[i + 1]); i += 2; continue; }
                    sb.Append(t[i++]);
                }
                toks.Add(new Tok { Kind = 's', Start = s, End = i + 1, Text = sb.ToString() });
            }
        }
    }

    int Match(int open)
    {
        int depth = 0;
        for (int i = open; i < toks.Count; i++)
        {
            if (toks[i].Kind == '{') depth++;
            else if (toks[i].Kind == '}' && --depth == 0) return i;
        }
        return -1;
    }

    // Finds a direct child of the block opened at token `open` (-1 = top level). Returns token index of the key.
    int Child(int open, string key)
    {
        int end = open < 0 ? toks.Count : Match(open);
        for (int i = open + 1; i < end; i++)
        {
            if (toks[i].Kind != 's') continue;
            if (i + 1 >= toks.Count) return -1;
            bool isMatch = string.Equals(toks[i].Text, key, StringComparison.OrdinalIgnoreCase);
            if (toks[i + 1].Kind == '{') { if (isMatch) return i; i = Match(i + 1); }
            else { if (isMatch) return i; i++; }
        }
        return -1;
    }

    int Block(params string[] path)
    {
        int open = -1;
        foreach (var p in path)
        {
            int k = Child(open, p);
            if (k < 0 || k + 1 >= toks.Count || toks[k + 1].Kind != '{') return -1;
            open = k + 1;
        }
        return open;
    }

    static readonly string[] AppsPath = { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps" };

    public string GetAppValue(string appId, string key)
    {
        int app = Block(AppsPath.Concat(new[] { appId }).ToArray());
        if (app < 0) return null;
        int k = Child(app, key);
        return k >= 0 && toks[k + 1].Kind == 's' ? toks[k + 1].Text : null;
    }

    public bool SetAppValue(string appId, string key, string value)
    {
        int apps = Block(AppsPath);
        if (apps < 0) return false;
        int depth = 0;
        for (int i = 0; i <= apps; i++) if (toks[i].Kind == '{') depth++; else if (toks[i].Kind == '}') depth--;
        int app = Block(AppsPath.Concat(new[] { appId }).ToArray());
        string ind = new string('\t', depth);
        if (app < 0)
        {
            int close = Match(apps);
            Text = Text.Insert(LineStart(toks[close].Start),
                ind + "\"" + appId + "\"\n" + ind + "{\n" + ind + "\t\"" + key + "\"\t\t\"" + value + "\"\n" + ind + "}\n");
        }
        else
        {
            int k = Child(app, key);
            if (k >= 0 && toks[k + 1].Kind == 's')
            {
                var v = toks[k + 1];
                Text = Text.Substring(0, v.Start) + "\"" + value + "\"" + Text.Substring(v.End);
            }
            else
            {
                int close = Match(app);
                Text = Text.Insert(LineStart(toks[close].Start), ind + "\t\"" + key + "\"\t\t\"" + value + "\"\n");
            }
        }
        Tokenize();
        return true;
    }

    // Raw value edits retain every unrelated byte, including comments and whitespace.
    public string RawValue(params string[] path)
    {
        Validate();
        int parent = path.Length == 1 ? -1 : Block(path.Take(path.Length - 1).ToArray());
        if (path.Length > 1 && parent < 0) return null;
        int key = Child(parent, path[path.Length - 1]);
        if (key < 0) return null;
        int end = toks[key + 1].Kind == '{' ? Match(key + 1) : key + 1;
        return Text.Substring(toks[key + 1].Start, toks[end].End - toks[key + 1].Start);
    }

    public string StringValue(params string[] path)
    {
        string raw = RawValue(path);
        if (raw == null || !raw.StartsWith("\"")) return null;
        var value = new VdfText("\"value\" " + raw);
        return value.toks[1].Text;
    }

    public void Validate()
    {
        int depth = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.End > Text.Length) throw new InvalidDataException("Truncated VDF string");
            if (t.Kind == '{') depth++;
            if (t.Kind == '}' && --depth < 0) throw new InvalidDataException("Unbalanced VDF");
            if (t.Kind == 's')
            {
                if (++i >= toks.Count || (toks[i].Kind != 's' && toks[i].Kind != '{')) throw new InvalidDataException("Missing VDF value");
                if (toks[i].End > Text.Length) throw new InvalidDataException("Truncated VDF value");
                if (toks[i].Kind == '{') depth++;
            }
        }
        if (depth != 0) throw new InvalidDataException("Unbalanced VDF");
    }

    int LineStart(int pos)
    {
        while (pos > 0 && Text[pos - 1] != '\n') pos--;
        return pos;
    }

}
