using System;
using System.Runtime.InteropServices;

namespace KotH.Tools;

/// <summary>
/// ImageFont.truetype of Pillow 12.3.0 with the raqm layout (_imagingft.c): the system's
/// FreeType and raqm, called the way Pillow calls them, so text comes out as Pillow draws it.
/// Only what the tools use: one line, horizontal, anchor "la", mode L.
/// </summary>
public sealed unsafe class PilFont
{
    const string FT = "libfreetype.so.6", Raqm = "libraqm.so.0";
    const int LoadDefault = 0, LoadRender = 0x4, LoadNoBitmap = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    struct SizeRequest
    {
        public int Type;
        public nint Width, Height;
        public uint HoriResolution, VertResolution;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BBox { public nint XMin, YMin, XMax, YMax; }

    [StructLayout(LayoutKind.Sequential)]
    struct Vector { public nint X, Y; }

    // FT_Bitmap.
    [StructLayout(LayoutKind.Sequential)]
    struct Bitmap
    {
        public uint Rows, Width;
        public int Pitch;
        public byte* Buffer;
        public ushort NumGrays;
        public byte PixelMode, PaletteMode;
        public void* Palette;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RaqmGlyph
    {
        public uint Index;
        public int XAdvance, YAdvance, XOffset, YOffset;
        public uint Cluster;
        public nint Face;
    }

    struct GlyphInfo { public int Index, XOffset, XAdvance, YOffset, YAdvance; }

    [DllImport(FT)] static extern int FT_Init_FreeType(out nint library);
    [DllImport(FT)] static extern int FT_New_Face(nint library, string path, nint index, out nint face);
    [DllImport(FT)] static extern int FT_Request_Size(nint face, ref SizeRequest request);
    [DllImport(FT)] static extern int FT_Load_Glyph(nint face, uint index, int flags);
    [DllImport(FT)] static extern int FT_Get_Glyph(nint slot, out nint glyph);
    [DllImport(FT)] static extern void FT_Glyph_Get_CBox(nint glyph, uint mode, out BBox box);
    [DllImport(FT)] static extern void FT_Done_Glyph(nint glyph);
    [DllImport(FT)] static extern int FT_Stroker_New(nint library, out nint stroker);
    [DllImport(FT)] static extern void FT_Stroker_Set(nint stroker, nint radius, int cap, int join, nint miterLimit);
    [DllImport(FT)] static extern void FT_Stroker_Done(nint stroker);
    [DllImport(FT)] static extern int FT_Glyph_StrokeBorder(ref nint glyph, nint stroker, byte inside, byte destroy);
    [DllImport(FT)] static extern int FT_Glyph_To_Bitmap(ref nint glyph, int mode, ref Vector origin, byte destroy);
    [DllImport(FT)] static extern uint FT_Get_Char_Index(nint face, nuint charcode);
    [DllImport(FT)] static extern int FT_Get_Kerning(nint face, uint left, uint right, uint mode, out Vector kerning);
    [DllImport(Raqm)] static extern nint raqm_create();
    [DllImport(Raqm)] static extern void raqm_destroy(nint rq);
    [DllImport(Raqm)] static extern byte raqm_set_text(nint rq, uint* text, nuint length);
    [DllImport(Raqm)] static extern byte raqm_set_par_direction(nint rq, int direction);
    [DllImport(Raqm)] static extern byte raqm_set_freetype_face(nint rq, nint face);
    [DllImport(Raqm)] static extern byte raqm_layout(nint rq);
    [DllImport(Raqm)] static extern RaqmGlyph* raqm_get_glyphs(nint rq, out nuint length);

    // Offsets into FreeType's records on 64-bit Linux (freetype.h, ftglyph.h).
    const int FaceFlags = 16, SlotHoriAdvance = 80, FaceGlyph = 152, FaceSize = 160, SizeAscender = 48;
    const int SlotBitmap = 152, SlotBitmapLeft = 192, SlotBitmapTop = 196;
    const int BitmapGlyphLeft = 40, BitmapGlyphTop = 44, BitmapGlyphBitmap = 48;

    static nint library;
    readonly nint face;
    readonly bool basic;

    /// <summary>ImageFont.truetype(path, size); basic: Layout.BASIC, as load_default() uses it.</summary>
    public PilFont(string path, float size, bool basic = false)
    {
        this.basic = basic;
        if (library == 0 && FT_Init_FreeType(out library) != 0)
            throw new InvalidOperationException("FreeType");
        Check(FT_New_Face(library, path, 0, out face));
        var request = new SizeRequest { Type = 0, Width = (nint)(size * 64), Height = (nint)(size * 64) };
        Check(FT_Request_Size(face, ref request));
    }

    static void Check(int error)
    {
        if (error != 0)
            throw new InvalidOperationException($"FreeType error {error}");
    }

    static int Pixel(long x) => (int)(((x + 32) & -64) >> 6);

    nint Slot => *(nint*)(face + FaceGlyph);

    // text_layout_fallback: glyph by glyph with FreeType's kerning.
    GlyphInfo[] LayoutBasic(string text)
    {
        int[] codepoints = [.. EnumerateRunes(text)];
        bool kerning = (*(nint*)(face + FaceFlags) & (1 << 6)) != 0;
        uint last = 0;
        var info = new GlyphInfo[codepoints.Length];
        for (int i = 0; i < info.Length; i++)
        {
            info[i].Index = (int)FT_Get_Char_Index(face, (nuint)codepoints[i]);
            Check(FT_Load_Glyph(face, (uint)info[i].Index, LoadDefault));
            if (kerning && last != 0 && info[i].Index != 0 && FT_Get_Kerning(face, last, (uint)info[i].Index, 0, out Vector delta) == 0)
            {
                info[i - 1].XAdvance += Pixel(delta.X);
                info[i - 1].YAdvance += Pixel(delta.Y);
            }
            info[i].XAdvance = (int)*(nint*)(Slot + SlotHoriAdvance);
            last = (uint)info[i].Index;
        }
        return info;
    }

    // text_layout_raqm.
    GlyphInfo[] Layout(string text)
    {
        if (basic)
            return LayoutBasic(text);
        int[] codepoints = [.. EnumerateRunes(text)];
        nint rq = raqm_create();
        try
        {
            fixed (int* p = codepoints)
                if (raqm_set_text(rq, (uint*)p, (nuint)codepoints.Length) == 0)
                    throw new InvalidOperationException("raqm_set_text");
            if (raqm_set_par_direction(rq, 0) == 0 || raqm_set_freetype_face(rq, face) == 0 || raqm_layout(rq) == 0)
                throw new InvalidOperationException("raqm");
            RaqmGlyph* glyphs = raqm_get_glyphs(rq, out nuint count);
            var info = new GlyphInfo[(int)count];
            for (int i = 0; i < info.Length; i++)
                info[i] = new GlyphInfo
                {
                    Index = (int)glyphs[i].Index, XOffset = glyphs[i].XOffset, XAdvance = glyphs[i].XAdvance,
                    YOffset = glyphs[i].YOffset, YAdvance = glyphs[i].YAdvance,
                };
            return info;
        }
        finally
        {
            raqm_destroy(rq);
        }
    }

    static System.Collections.Generic.IEnumerable<int> EnumerateRunes(string text)
    {
        foreach (System.Text.Rune rune in text.EnumerateRunes())
            yield return rune.Value;
    }

    /// <summary>FreeTypeFont.getlength: the advance in pixels, 1/64 precise.</summary>
    public double GetLength(string text)
    {
        int length = 0;
        foreach (GlyphInfo g in Layout(text))
            length += g.XAdvance;
        return length / 64.0;
    }

    /// <summary>FreeTypeFont.getbbox(text, stroke_width) with anchor "la".</summary>
    public (double Left, double Top, double Right, double Bottom) GetBBox(string text, double strokeWidth = 0)
    {
        var (width, height, x, y) = BoundingBox(Layout(text), LoadDefault);
        double left = x - strokeWidth, top = y - strokeWidth;
        return (left, top, left + width + 2 * strokeWidth, top + height + 2 * strokeWidth);
    }

    // bounding_box_and_anchors, horizontal, anchor "la".
    (long Width, long Height, int XOffset, int YOffset) BoundingBox(GlyphInfo[] glyphs, int loadFlags)
    {
        long position = 0;
        int xMin = 0, xMax = 0, yMin = 0, yMax = 0;
        foreach (GlyphInfo g in glyphs)
        {
            int px = Pixel(position + g.XOffset), py = Pixel(g.YOffset);
            position += g.XAdvance;
            long advanced = Pixel(position);
            if (advanced > xMax)
                xMax = (int)advanced;
            Check(FT_Load_Glyph(face, (uint)g.Index, loadFlags));
            Check(FT_Get_Glyph(Slot, out nint glyph));
            FT_Glyph_Get_CBox(glyph, 3, out BBox box);
            if (box.XMax + px > xMax)
                xMax = (int)(box.XMax + px);
            if (box.XMin + px < xMin)
                xMin = (int)(box.XMin + px);
            if (box.YMax + py > yMax)
                yMax = (int)(box.YMax + py);
            if (box.YMin + py < yMin)
                yMin = (int)(box.YMin + py);
            FT_Done_Glyph(glyph);
        }
        int yAnchor = glyphs.Length > 0 ? Pixel(*(nint*)(*(nint*)(face + FaceSize) + SizeAscender)) : 0;
        return ((long)xMax - xMin, (long)yMax - yMin, xMin, -(-yAnchor + yMax));
    }

    /// <summary>ImageDraw.text(xy, text, fill=ink, font=this, stroke_width, stroke_fill=ink): one line, anchor "la".</summary>
    public void Draw(PilDraw draw, double x, double y, string text, byte[] ink, float strokeWidth)
    {
        var (mask, ox, oy) = Render(text, strokeWidth, true, (float)(x - Math.Truncate(x)), (float)(y - Math.Truncate(y)));
        draw.Bitmap((int)x + ox, (int)y + oy, mask, ink);
    }

    /// <summary>
    /// font.render (FreeTypeFont.getmask2) in mode L: the text's mask and where it goes
    /// relative to the anchor. Strokes as Pillow does with stroke_filled.
    /// </summary>
    public (PilImage Mask, int XOffset, int YOffset) Render(string text, float strokeWidth, bool strokeFilled, float xStart, float yStart)
    {
        GlyphInfo[] glyphs = Layout(text);
        int loadFlags = strokeWidth != 0 ? LoadNoBitmap : LoadDefault;
        var (width, height, xOffset, yOffset) = BoundingBox(glyphs, loadFlags);
        width += (long)Math.Ceiling(strokeWidth * 2 + xStart);
        height += (long)Math.Ceiling(strokeWidth * 2 + yStart);
        var im = new PilImage("L", (int)width, (int)height);
        xOffset = (int)Math.Round(xOffset - strokeWidth, MidpointRounding.AwayFromZero);
        yOffset = (int)Math.Round(yOffset - strokeWidth, MidpointRounding.AwayFromZero);
        if (glyphs.Length == 0 || width == 0 || height == 0)
            return (im, xOffset, yOffset);
        nint stroker = 0;
        if (strokeWidth != 0)
        {
            Check(FT_Stroker_New(library, out stroker));
            FT_Stroker_Set(stroker, (nint)Math.Round(strokeWidth * 64, MidpointRounding.AwayFromZero), 1, 0, 0);
        }
        int x = 0, y = 0, xMin = 0, yMax = 0;
        foreach (GlyphInfo g in glyphs)
        {
            int px = Pixel(x + g.XOffset), py = Pixel(y + g.YOffset);
            Check(FT_Load_Glyph(face, (uint)g.Index, loadFlags | LoadRender));
            nint slot = Slot;
            int top = *(int*)(slot + SlotBitmapTop), left = *(int*)(slot + SlotBitmapLeft);
            if (top + py > yMax)
                yMax = top + py;
            if (left + px < xMin)
                xMin = left + px;
            x += g.XAdvance;
            y += g.YAdvance;
        }
        x = (int)Math.Round((-xMin + strokeWidth + xStart) * 64, MidpointRounding.AwayFromZero);
        y = (int)Math.Round((-yMax + -strokeWidth - yStart) * 64, MidpointRounding.AwayFromZero);
        if (stroker == 0)
            loadFlags |= LoadRender;
        foreach (GlyphInfo g in glyphs)
        {
            int px = Pixel(x + g.XOffset), py = Pixel(y + g.YOffset);
            Check(FT_Load_Glyph(face, (uint)g.Index, loadFlags));
            nint slot = Slot, glyph = 0;
            Bitmap bitmap;
            int xx, yy;
            if (stroker != 0)
            {
                Check(FT_Get_Glyph(slot, out glyph));
                Check(strokeFilled ? FT_Glyph_StrokeBorder(ref glyph, stroker, 0, 1) : throw new NotSupportedException("stroke"));
                var origin = new Vector();
                Check(FT_Glyph_To_Bitmap(ref glyph, 0, ref origin, 1));
                bitmap = *(Bitmap*)(glyph + BitmapGlyphBitmap);
                xx = px + *(int*)(glyph + BitmapGlyphLeft);
                yy = -(py + *(int*)(glyph + BitmapGlyphTop));
            }
            else
            {
                bitmap = *(Bitmap*)(slot + SlotBitmap);
                xx = px + *(int*)(slot + SlotBitmapLeft);
                yy = -(py + *(int*)(slot + SlotBitmapTop));
            }
            if (bitmap.Buffer != null)
            {
                if (bitmap.PixelMode != 2)
                    throw new NotSupportedException($"pixel mode {bitmap.PixelMode}");
                int x0 = 0, x1 = (int)bitmap.Width;
                if (xx < 0)
                    x0 = -xx;
                if (xx + x1 > im.Width)
                    x1 = im.Width - xx;
                byte* source = bitmap.Buffer;
                for (uint by = 0; by < bitmap.Rows; by++, yy++, source += bitmap.Pitch)
                {
                    if (yy < 0 || yy >= im.Height)
                        continue;
                    for (int k = x0; k < x1; k++)
                    {
                        uint src = source[k];
                        if (src == 0)
                            continue;
                        ref byte target = ref im.Data[yy * im.Width + xx + k];
                        target = target > 0 ? PilImage.Clip8((int)(src + PilImage.MulDiv255(target, 255 - src))) : (byte)src;
                    }
                }
            }
            x += g.XAdvance;
            y += g.YAdvance;
            if (stroker != 0)
                FT_Done_Glyph(glyph);
        }
        if (stroker != 0)
            FT_Stroker_Done(stroker);
        return (im, xOffset, yOffset);
    }
}
