using System.Text;

namespace GC.Infrastructure;

// A bounded reader for serialized Enfusion config objects, not an inheritance resolver.
internal sealed class MissionConfig
{
    internal sealed record Entry(string[] Values, MissionConfig? Body);
    public List<Entry> Entries { get; } = [];
    public Entry? Find(string key)
    {
        var found = Entries.Where(e => e.Values.FirstOrDefault() == key).Take(2).ToArray();
        if (found.Length > 1) throw new InvalidDataException("Duplicate config field: " + key);
        return found.FirstOrDefault();
    }
    public string Text(string key) => Find(key) is { Values.Length: > 1 } e ? string.Join("\n", e.Values.Skip(1)) : "";
    public MissionConfig? Object(string key) => Find(key)?.Body;
    public string[] Array(string key) => Object(key)?.Entries.SelectMany(e => e.Values).ToArray() ?? [];

    public static MissionConfig Read(string text)
    {
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Mission header too large");
        var tokens = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i++];
            if (c == '\r' || c == ' ' || c == '\t') continue;
            if (c == '/' && i < text.Length && text[i] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && i < text.Length && text[i] == '*')
            {
                var end = text.IndexOf("*/", i + 1, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Unclosed config comment");
                i = end + 2; continue;
            }
            if (c == '\\') // Serialized multiline strings are joined with a newline.
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                continue;
            }
            if (c is '{' or '}' or '\n') { tokens.Add(c.ToString()); continue; }
            if (c == '"')
            {
                var value = new StringBuilder(); var closed = false;
                while (i < text.Length)
                {
                    c = text[i++];
                    if (c == '"') { closed = true; break; }
                    if (c == '\\' && i < text.Length)
                    {
                        c = text[i++]; value.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c); 
                    }
                    else value.Append(c);
                }
                if (!closed) throw new InvalidDataException("Unclosed config string");
                // Prefix distinguishes quoted braces and newlines from structure.
                tokens.Add("\0" + value); continue;
            }
            var start = i - 1;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}') i++;
            tokens.Add(text[start..i]);
        }
        var position = 0;
        MissionConfig Body(int depth, bool nested)
        {
            if (depth > 32) throw new InvalidDataException("Config nesting too deep");
            var result = new MissionConfig(); var values = new List<string>();
            void Flush() { if (values.Count > 0) { result.Entries.Add(new(values.ToArray(), null)); values.Clear(); } }
            while (position < tokens.Count)
            {
                var token = tokens[position++];
                if (token == "\n") { Flush(); continue; }
                if (token == "}") { Flush(); if (!nested) throw new InvalidDataException("Unexpected config end"); return result; }
                if (token == "{")
                {
                    if (values.Count == 0) throw new InvalidDataException("Unnamed config object");
                    var names = values.ToArray(); values.Clear(); result.Entries.Add(new(names, Body(depth + 1, true)));
                }
                else values.Add(token.StartsWith('\0') ? token[1..] : token);
            }
            if (nested) throw new InvalidDataException("Unclosed config object");
            Flush(); return result;
        }
        var root = Body(0, false);
        // Header subclasses (including Scenario Framework/modded types) share this serialized shape.
        var header = root.Entries.Count == 1 && root.Entries[0].Body is not null ? root.Entries[0] : null;
        if (header is null) return root; // Supports the unwrapped headers used by import tests.
        if (header.Values.Length != 1 || header.Body is null) throw new InvalidDataException("Inherited mission headers require a resolved export");
        return header.Body;
    }
}
