using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NetworkAutoSwitch.Tray;

enum Weight { Regular, SemiBold, Bold }

/// <summary>Loads the bundled Inter font (three static weights, embedded in the exe) for this process only.
/// Two registrations are needed: GDI+ (Graphics.DrawString, Font objects) only sees a PrivateFontCollection, while
/// GDI (TextRenderer, native controls) only sees fonts added with AddFontMemResourceEx. Both stay private to the app.</summary>
static class AppFonts
{
    [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    static readonly PrivateFontCollection _pfc = new();
    static FontFamily? _regular, _semi;      // "Inter" holds Regular and Bold; the static SemiBold file is its own family ("Inter SemiBold")
    /// <summary>True when Inter was loaded; false = the app is using Segoe UI (the selftest checks this).</summary>
    public static bool Loaded { get; private set; }
    public static string Report { get; private set; } = "not loaded";

    static AppFonts()
    {
        try
        {
            var asm = typeof(AppFonts).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                using var s = asm.GetManifestResourceStream(name)!;
                var bytes = new byte[s.Length]; s.ReadExactly(bytes);
                // the memory must live as long as the fonts are used (the whole run): it is never freed on purpose
                IntPtr mem = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                _pfc.AddMemoryFont(mem, bytes.Length);
                uint n = 0; AddFontMemResourceEx(mem, (uint)bytes.Length, IntPtr.Zero, ref n);
            }
            _regular = _pfc.Families.FirstOrDefault(f => f.Name == "Inter");
            _semi = _pfc.Families.FirstOrDefault(f => f.Name == "Inter SemiBold") ?? _regular;
            Loaded = _regular is not null;
            Report = "families: " + string.Join(", ", _pfc.Families.Select(f => f.Name));
        }
        catch (Exception e) { Loaded = false; Report = "failed: " + e.Message; }
    }

    public static Font Make(float pt, Weight w)
    {
        try
        {
            if (Loaded)
                return w switch
                {
                    // built from the FontFamily object: a Font made by name alone silently falls back to another face for private fonts
                    Weight.SemiBold when _semi != _regular => new Font(_semi!, pt, FontStyle.Regular),
                    Weight.SemiBold or Weight.Bold => new Font(_regular!, pt, FontStyle.Bold),
                    _ => new Font(_regular!, pt, FontStyle.Regular),
                };
        }
        catch { }
        return w == Weight.Regular ? new Font("Segoe UI", pt) : new Font(w == Weight.Bold ? "Segoe UI" : "Segoe UI Semibold", pt, w == Weight.Bold ? FontStyle.Bold : FontStyle.Regular);
    }
}
