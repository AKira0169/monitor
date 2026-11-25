using System;
using System.Collections.Generic;

// A built-in workaround or note for one monitor model or vendor. Looked up by ShortId (PnP manufacturer code
// + product code, e.g. GSM5B7F): a Key that equals the ShortId matches exactly, a shorter Key is a prefix
// (e.g. "GSM" = every LG model). The longest matching Key wins.
class Quirk
{
    public string Key;
    public string Vendor;              // for logs and SUPPORTED.md
    public string Note;                // shown to the user when the monitor can't be controlled, and in SUPPORTED.md
    public int[] FixedInputs { get; set; }        // replaces the capabilities string (when it is wrong or missing)
    public Dictionary<int, int> Remap { get; set; } // code shown in the app -> code actually written to VCP 0x60
    public int DelayMs { get; set; }           // extra pause after each write
    public bool Unsupported;           // switching needs something dxva2 can't send (documented, not faked)

    public int ToDevice(int shown)
    {
        int v;
        return Remap != null && Remap.TryGetValue(shown, out v) ? v : shown;
    }

    public int FromDevice(int device)
    {
        if (Remap != null)
            foreach (var kv in Remap) if (kv.Value == device) return kv.Key;
        return device;
    }
}

static class Quirks
{
    // Only entries backed by well-known DDC/CI behaviour belong here. Confidence is marked per entry.
    // Both current entries are note-only: no remap, fixed list or delay is applied. Those mechanisms exist
    // (Remap, FixedInputs, DelayMs) and are honoured by Ddc, but no built-in model needs them with certainty.
    static readonly Quirk[] Table =
    {
        // CONFIDENCE: medium. "GSM" is LG's PnP code. Several LG models list or expect non-standard VCP 0x60
        // values (0x90/0x91 for HDMI, 0xD0/0xD1 for DisplayPort) instead of the MCCS 0x11/0x12 and 0x0F/0x10.
        // If the capabilities string lists them, they just work (Ddc.InputName names them). Some other LG
        // models switch only through vendor VCP 0xF4 with a special address, which dxva2 SetVCPFeature
        // cannot send, so that variant is NOT supported. UNCERTAIN which models need which.
        new Quirk
        {
            Key = "GSM", Vendor = "LG",
            Note = "Some LG models use non-standard input codes (0x90/0x91 HDMI, 0xD0/0xD1 DisplayPort) or a vendor-specific switch command that Windows' DDC API cannot send. If input switching does nothing, send log.txt."
        },

        // CONFIDENCE: medium-high. "APP" is Apple's PnP code. Apple displays generally do not answer DDC/CI
        // over DisplayPort/Thunderbolt/HDMI on Windows (brightness is controlled over USB), so expect no control.
        new Quirk
        {
            Key = "APP", Vendor = "Apple", Unsupported = true,
            Note = "Apple displays generally do not support DDC/CI on Windows (brightness is sent over USB instead)."
        },

        // Example of the remaining mechanisms (kept as a template, deliberately not active):
        // new Quirk { Key = "XYZ1234", Vendor = "Example", FixedInputs = new[] { 0x0F, 0x11 },
        //             Remap = new Dictionary<int, int> { { 0x11, 0x90 } }, DelayMs = 100, Note = "..." },
    };

    public static IEnumerable<Quirk> All { get { return Table; } }

    public static Quirk Find(string shortId)
    {
        if (string.IsNullOrEmpty(shortId)) return null;
        Quirk best = null;
        foreach (var q in Table)
            if (shortId.StartsWith(q.Key, StringComparison.OrdinalIgnoreCase) && (best == null || q.Key.Length > best.Key.Length))
                best = q;
        return best;
    }
}
