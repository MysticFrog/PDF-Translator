using Microsoft.Win32;
using PdfSharp.Fonts;

namespace PdfTranslator.Core;

/// <summary>
/// Resolves PDFsharp font requests to installed Windows fonts (system and per-user), using the font list in the
/// registry. Faces inside .ttc collections are extracted to a standalone TrueType file, which PDFsharp can embed.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    sealed record Face(string File, int Index);

    readonly Dictionary<string, Face> _faces = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _data = new(StringComparer.OrdinalIgnoreCase);

    public WindowsFontResolver()
    {
        string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        Load(Registry.LocalMachine, sysDir);
        Load(Registry.CurrentUser, userDir);
    }

    void Load(RegistryKey root, string dir)
    {
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
        if (key == null) return;
        foreach (var valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is not string file) continue;
            string path = Path.IsPathRooted(file) ? file : Path.Combine(dir, file);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".ttf" or ".ttc" or ".otf") || !File.Exists(path)) continue;

            string names = valueName.Replace("(TrueType)", "").Replace("(OpenType)", "").Trim();
            var parts = names.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                _faces.TryAdd(parts[i], new Face(path, ext == ".ttc" ? i : 0));
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        string style = (isBold, isItalic) switch
        {
            (true, true) => " Bold Italic",
            (true, false) => " Bold",
            (false, true) => " Italic",
            _ => "",
        };
        if (_faces.ContainsKey(familyName + style)) return new FontResolverInfo(familyName + style);
        if (isBold && isItalic && _faces.ContainsKey(familyName + " Bold"))
            return new FontResolverInfo(familyName + " Bold", false, true);
        foreach (var regular in new[] { familyName, familyName + " Regular" })
            if (_faces.ContainsKey(regular)) return new FontResolverInfo(regular, isBold, isItalic);
        if (!familyName.Equals("Arial", StringComparison.OrdinalIgnoreCase))
            return ResolveTypeface("Arial", isBold, isItalic);
        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        lock (_data)
        {
            if (_data.TryGetValue(faceName, out var cached)) return cached;
            if (!_faces.TryGetValue(faceName, out var face)) return null;
            var bytes = File.ReadAllBytes(face.File);
            if (face.File.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) bytes = ExtractFromCollection(bytes, face.Index);
            _data[faceName] = bytes;
            return bytes;
        }
    }

    /// <summary>Rebuilds one face of a TrueType collection as a standalone font file.</summary>
    static byte[] ExtractFromCollection(byte[] ttc, int index)
    {
        static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        static ushort U16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);
        static void W32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        if (ttc.Length < 12 || U32(ttc, 0) != 0x74746366) return ttc; // not 'ttcf'
        int count = (int)U32(ttc, 8);
        int offset = (int)U32(ttc, 12 + 4 * Math.Clamp(index, 0, count - 1));
        int numTables = U16(ttc, offset + 4);
        int headerLen = 12 + 16 * numTables;

        var tables = new List<(int recOffset, int srcOffset, int length)>();
        int total = headerLen;
        for (int i = 0; i < numTables; i++)
        {
            int rec = offset + 12 + 16 * i;
            int len = (int)U32(ttc, rec + 12);
            tables.Add((rec, (int)U32(ttc, rec + 8), len));
            total += (len + 3) & ~3;
        }

        var outBytes = new byte[total];
        Array.Copy(ttc, offset, outBytes, 0, headerLen);
        int pos = headerLen;
        for (int i = 0; i < tables.Count; i++)
        {
            var (_, src, len) = tables[i];
            Array.Copy(ttc, src, outBytes, pos, len);
            W32(outBytes, 12 + 16 * i + 8, (uint)pos);
            pos += (len + 3) & ~3;
        }
        return outBytes;
    }
}
