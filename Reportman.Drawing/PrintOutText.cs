#region Copyright
/*
 *  Report Manager:  Database Reporting tool for .Net and Mono
 *
 *     The contents of this file are subject to the MPL License
 *     with optional use of GPL or LGPL licenses.
 *     You may not use this file except in compliance with the
 *     Licenses. You may obtain copies of the Licenses at:
 *     http://reportman.sourceforge.net/license
 *
 *     Software is distributed on an "AS IS" basis,
 *     WITHOUT WARRANTY OF ANY KIND, either
 *     express or implied.  See the License for the specific
 *     language governing rights and limitations.
 *
 *  Copyright (c) 1994 - 2008 Toni Martir (toni@reportman.es)
 *  All Rights Reserved.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Reportman.Drawing
{
    /// <summary>
    /// Render driver that paints a report metafile as plain or escape-coded text for line/dot-matrix
    /// and receipt printers, mapping fonts to character-per-inch steps and emitting the printer
    /// control codes (EPSON, IBM ProPrinter, HP-PCL, PLAIN, etc.) into a byte stream.
    /// </summary>
    public class PrintOutText : PrintOut, IDisposable
    {
        SortedList<PrintStepType, PrintStepType> allowedsteps;
        private int FLinesPerInch;
        private System.IO.MemoryStream currpageStream;
        private bool FDrawerBefore;
        private bool FDrawerAfter;
        /// <summary>
        /// Width of the printable page area, expressed in twips.
        /// </summary>
        public int FPageWidth;
        private int PageQt;
        /// <summary>
        /// Height of the printable page area, expressed in twips.
        /// </summary>
        public int FPageHeight;
        /// <summary>
        /// Default vertical print density in lines per inch used when no value is supplied by the metafile.
        /// </summary>
        public const int DEFAULT_LINESPERINCH = 6;
        private List<PrintLine> Lines;
        private PrinterSelectType FPrinterSelect;
        /// <summary>
        /// When set to a non-empty value, forces the printer driver to use instead of the one selected
        /// by the printer configuration.
        /// </summary>
        public string ForceDriverName;
        /// <summary>
        /// Name of the printer driver whose escape codes are currently emitted (for example EPSON,
        /// IBMPROPRINTER, HP-PCL or PLAIN).
        /// </summary>
        public string FPrinterDriver;
        private bool LoadOEMConvert;
        private bool OEMConvert;
        /// <summary>
        /// When true, output is written as plain text, trimming style attributes and omitting printer
        /// escape codes.
        /// </summary>
        public bool FullPlain;
        /// <summary>
        /// Count of non-empty lines written during the current print run.
        /// </summary>
        public int blacklines;
        /// <summary>
        /// Count of empty lines written during the current print run.
        /// </summary>
        public int whitelines;
        private PrintLine PreviousLine;
        private List<LineInfo> linfos;
        bool masterselect;
        bool limitedmaster;
        bool condensedmaster;
        /// <summary>
        /// Byte stream that accumulates the rendered printer output, including any escape codes.
        /// </summary>
        public System.IO.MemoryStream PrintResultStream;
        /// <summary>
        /// Gets the rendered printer output decoded as a string using code page 437.
        /// </summary>
        public string PrintResult {
            get
            {
                byte[] nbytes = PrintResultStream.ToArray();

                return Encoding.GetEncoding(437).GetString(nbytes);
            }

        }
        private SortedList<PrinterRawOperation, byte[]> escapecodes;
        byte master10;
        byte master12;
        byte mastercond;
        byte masterwide;
        int linefeeds;
        System.IO.MemoryStream FResultStream;
        /// <summary>
        /// Constructo and initialization
        /// </summary>
        public PrintOutText()
            : base()
        {
            PrintResultStream = new System.IO.MemoryStream();
            linfos = new List<LineInfo>();
            FLinesPerInch = DEFAULT_LINESPERINCH;
            Lines = new List<PrintLine>();
            ForceDriverName = "";
            OEMConvert = false;
            FullPlain = false;
            LoadOEMConvert = true;
            FPrinterDriver = "PLAIN";
            escapecodes = new SortedList<PrinterRawOperation, byte[]>();
            master10 = 0;
            master12 = 1;
            mastercond = 4;
            masterwide = 32;
            linefeeds = 0;
        }
        static PrintOutText()
        {
#if NETCOREAPP
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
        }
        private static byte[] emptyByteArray = { };
        private static byte[] spaceByteArray = { 32 };
        /// <summary>
        /// Returns the escape byte sequence configured for the given raw printer operation, or an empty
        /// array when the operation is not defined for the active driver.
        /// </summary>
        /// <param name="op">Raw printer operation to look up.</param>
        /// <returns>The escape bytes for the operation, or an empty array if none.</returns>
        public byte[] GetEscape(PrinterRawOperation op)
        {
            int index = escapecodes.IndexOfKey(op);
            if (index >= 0)
                return escapecodes[op];
            else
                return emptyByteArray;
        }
        /// <summary>
        /// Draw all objects of the page to current PDF file page
        /// </summary>
        /// <param name="meta">MetaFile containing the page</param>
        /// <param name="page">MetaPage to be drawn</param>
        override public void DrawPage(MetaFile meta, MetaPage page)
        {
            for (int i = 0; i < page.Objects.Count; i++)
            {
                DrawObject(meta, page, page.Objects[i]);
            }

        }
        /// <summary>
        /// Draws a single metafile object onto the current page; only text objects produce output in the
        /// text driver.
        /// </summary>
        /// <param name="meta">MetaFile that owns the object.</param>
        /// <param name="page">MetaPage that contains the object.</param>
        /// <param name="aobj">MetaObject to draw.</param>
        public void DrawObject(MetaFile meta, MetaPage page, MetaObject aobj)
        {
            int posx, posy;
            Rectangle rec;
            int aalign;
            string astring;
            PrintStepType fontstep;
            bool red;
            posx = aobj.Left;
            posy = aobj.Top;
            switch (aobj.MetaType)
            {
                case MetaObjectType.Text:
                    MetaObjectText obj = (MetaObjectText)aobj;
                    aalign = obj.Alignment;
                    rec = new Rectangle(posx, posy, obj.Width, obj.Height);
                    astring = page.GetText(obj);
                    fontstep = FontSizeToStep(obj.FontSize, obj.PrintStep);
                    red = GraphicUtils.ColorFromInteger(obj.FontColor) == Color.Red;
                    TextRect(rec, astring, aalign, obj.CutText,
                    obj.WordWrap, obj.RightToLeft, fontstep, obj.FontStyle, red);

                    break;
                case MetaObjectType.Barcode:
                    MetaObjectBarcode bar = (MetaObjectBarcode)aobj;
                    if (SupportsNativeBarcode(bar.Symbology))
                        NativeBarcodeOut(bar, page.GetText(bar));
                    break;
                case MetaObjectType.Image:
                    // The ESC/POS receipt printers draw a raster image by themselves (GS v 0); the text driver
                    // used to skip images altogether, so a logo on a receipt never printed and left its rows blank.
                    MetaObjectImage img = (MetaObjectImage)aobj;
                    if (!img.PreviewOnly && ImageRasterizer != null && IsEscPosReceiptDriver(EffectiveDriverName()))
                        NativeImageOut(page, img);
                    break;
            }
        }
        /// <summary>
        /// Packed 1-bit rows of an image, as <c>GS v 0</c> wants them: MSB first, 1 = black, each row padded to a byte.
        /// </summary>
        public class RasterBits
        {
            /// <summary>Width in dots.</summary>
            public int Width;
            /// <summary>Height in dots (rows).</summary>
            public int Height;
            /// <summary>The rows, ((Width + 7) / 8) bytes each.</summary>
            public byte[] Bits;
        }
        /// <summary>
        /// Decodes an encoded image and fits it inside a box of dots as 1-bit rows. The text driver has no image
        /// decoder of its own: the host registers one (Reportman.Drawing.CrossPlatform.EscPosImagen with SkiaSharp).
        /// Null = images are skipped, as they always were.
        /// </summary>
        public static Func<byte[], int, int, RasterBits> ImageRasterizer;
        /// <summary>
        /// Widest raster an 80 mm ESC/POS receipt printer draws (TM-T88: 512 dots at 203 dpi across 72 mm; 576 on
        /// the 80 mm-wide print area of newer models). 512 is what every clone honours.
        /// </summary>
        public const int ESCPOS_MAX_RASTER_DOTS = 512;
        /// <summary>
        /// Draws an image object as a <c>GS v 0</c> raster attached to the print line its top falls on, aligned
        /// left/centre/right by where its box sits, and marks the lines its box covers as consumed (the printer
        /// feeds the paper while drawing), exactly like <see cref="NativeBarcodeOut"/>.
        /// </summary>
        private void NativeImageOut(MetaPage page, MetaObjectImage img)
        {
            if (Lines.Count == 0)
                return;
            byte[] data;
            using (System.IO.MemoryStream stream = page.GetStream(img))
            {
                if (stream == null || stream.Length == 0)
                    return;
                data = stream.ToArray();
            }
            int wDots = (int)Math.Round((double)img.Width * ESCPOS_DPI / Twips.TWIPS_PER_INCH);
            int hDots = (int)Math.Round((double)img.Height * ESCPOS_DPI / Twips.TWIPS_PER_INCH);
            if (wDots > ESCPOS_MAX_RASTER_DOTS) wDots = ESCPOS_MAX_RASTER_DOTS;
            if (wDots < 8 || hDots < 1)
                return;
            RasterBits raster;
            try { raster = ImageRasterizer(data, wDots, hDots); }
            catch { raster = null; }
            if (raster == null || raster.Bits == null || raster.Width < 1 || raster.Height < 1)
                return;

            int first = GetLineIndex(img.Top);
            if (first >= Lines.Count)
                first = Lines.Count - 1;
            int last = GetLineIndex(img.Top + img.Height) - 1;
            if (last >= Lines.Count)
                last = Lines.Count - 1;
            if (last < first)
                last = first;

            int center = img.Left + img.Width / 2;
            byte align = 0;
            if (Math.Abs(center - FPageWidth / 2) <= 120)
                align = 1;
            else if (center > FPageWidth / 2)
                align = 2;

            int stride = (raster.Width + 7) / 8;
            using (System.IO.MemoryStream s = new System.IO.MemoryStream())
            {
                s.Write(new byte[] { 27, (byte)'a', align }, 0, 3);                                           // ESC a n
                s.Write(new byte[] { 29, (byte)'v', (byte)'0', 0, (byte)(stride & 0xFF), (byte)(stride >> 8), (byte)(raster.Height & 0xFF), (byte)(raster.Height >> 8) }, 0, 8);   // GS v 0 m xL xH yL yH
                s.Write(raster.Bits, 0, stride * raster.Height);
                s.Write(new byte[] { 27, (byte)'a', 0 }, 0, 3);
                PrintLine line = Lines[first];
                if (line.RawBefore == null)
                    line.RawBefore = new List<byte[]>();
                line.RawBefore.Add(s.ToArray());
            }
            for (int i = first; i <= last; i++)
                Lines[i].Consumed = true;
        }
        /// <summary>
        /// The receipt drivers that speak ESC/POS: an Epson TM-T88 family printer, or any of the
        /// many that emulate it, draws a QR by itself from <c>GS ( k</c>. The dot-matrix TM-U210
        /// and the line printers do not, so they keep getting the modules as drawings — which the
        /// text driver cannot paint anyway, so on those the barcode is simply absent, as it always was.
        /// </summary>
        private static bool IsEscPosReceiptDriver(string drivername)
        {
            return IsTm88Driver(drivername);
        }
        /// <summary>
        /// The driver name that will be in force when the document prints. It is needed BEFORE
        /// <see cref="NewDocument"/> runs (the report asks while generating its first page), so it is
        /// resolved the same way UpdatePrinterConfig will resolve it: the forced name if any, else the
        /// printer configuration of the metafile's printer selection.
        /// </summary>
        private string EffectiveDriverName()
        {
            if (ForceDriverName.Length > 0)
                return ForceDriverName;
            if (CurrentMetafile != null)
                return PrinterConfig.GetDriverName(CurrentMetafile.PrinterSelect);
            return PrinterConfig.GetDriverName(FPrinterSelect);
        }
        /// <summary>
        /// A QR is drawn natively by the ESC/POS receipt drivers (see <see cref="IsEscPosReceiptDriver"/>);
        /// and with it everything ESC/POS draws by itself: PDF417 (GS ( k cn 48) and the linear
        /// symbologies of GS k (UPC-A/E, EAN-13/8, Code 39, ITF, Codabar, Code 93, Code 128).
        /// </summary>
        public override bool SupportsNativeBarcode(MetaBarcodeSymbology symbology)
        {
            return symbology != MetaBarcodeSymbology.Unknown && IsEscPosReceiptDriver(EffectiveDriverName());
        }
        /// <summary>
        /// Dots per inch the ESC/POS receipt printers print at (TM-T88, TM-T20 and their clones:
        /// 180 or 203 dpi; 203 is the common one and the module size only moves by one dot between
        /// the two for a 40 mm code).
        /// </summary>
        public const int ESCPOS_DPI = 203;
        /// <summary>
        /// Attaches a natively drawn barcode to the print line its top falls on, and marks the lines
        /// its box covers as consumed so the paper the printer feeds while drawing the symbol is not
        /// followed by the blank lines the layout reserved for it. Text on those lines still prints,
        /// after the symbol.
        /// </summary>
        /// <param name="bar">Barcode object of the page.</param>
        /// <param name="data">The data to encode.</param>
        private void NativeBarcodeOut(MetaObjectBarcode bar, string data)
        {
            if (Lines.Count == 0)
                return;
            int first = GetLineIndex(bar.Top);
            if (first >= Lines.Count)
                first = Lines.Count - 1;
            int last = GetLineIndex(bar.Top + bar.Height) - 1;
            if (last >= Lines.Count)
                last = Lines.Count - 1;
            if (last < first)
                last = first;

            // Alignment: ESC/POS justifies whole lines, so the box tells which of the three it is.
            // The centred case is the receipt case; the tolerance is one column at 12 cpi.
            int center = bar.Left + bar.Width / 2;
            byte align = 0;                                        // left
            if (Math.Abs(center - FPageWidth / 2) <= 120)
                align = 1;                                         // centre
            else if (center > FPageWidth / 2)
                align = 2;                                         // right

            byte[] symbol = bar.Symbology == MetaBarcodeSymbology.QR ? EncodeEscPosQr(data, bar, align)
                : bar.Symbology == MetaBarcodeSymbology.PDF417 ? EncodeEscPosPdf417(data, bar, align)
                : EncodeEscPos1D(data, bar, align);
            PrintLine line = Lines[first];
            if (line.RawBefore == null)
                line.RawBefore = new List<byte[]>();
            line.RawBefore.Add(symbol);
            for (int i = first; i <= last; i++)
                Lines[i].Consumed = true;
        }
        /// <summary>
        /// A linear symbology through <c>GS k</c>: bar height from the box, module width from the box
        /// over the modules the encoder counted (2..6, the range the command admits), no HRI text
        /// (the report item draws none), and the data as ISO-8859-1. Code 128 goes with the subset
        /// prefix the printer wants ({A/{B/{C); the automatic one is sent as B, which covers the
        /// printable ASCII the item accepts.
        /// </summary>
        public static byte[] EncodeEscPos1D(string data, MetaObjectBarcode bar, byte align)
        {
            byte m;
            switch (bar.Symbology)
            {
                case MetaBarcodeSymbology.UPCA: m = 65; break;
                case MetaBarcodeSymbology.UPCE: m = 66; break;
                case MetaBarcodeSymbology.EAN13: m = 67; break;
                case MetaBarcodeSymbology.EAN8: m = 68; break;
                case MetaBarcodeSymbology.Code39: m = 69; break;
                case MetaBarcodeSymbology.ITF: m = 70; break;
                case MetaBarcodeSymbology.Codabar: m = 71; break;
                case MetaBarcodeSymbology.Code93: m = 72; break;
                default: m = 73; break;                                 // Code 128
            }
            string payload = data ?? "";
            if (m == 73)
            {
                string prefix = bar.Columns == 1 ? "{A" : bar.Columns == 3 ? "{C" : "{B";
                if (!payload.StartsWith("{")) payload = prefix + payload;
            }
            byte[] bytes = Encoding.GetEncoding(28591).GetBytes(payload);
            if (bytes.Length > 255) Array.Resize(ref bytes, 255);

            int boxdots = (int)Math.Round((double)bar.Width * ESCPOS_DPI / Twips.TWIPS_PER_INCH);
            int modules = bar.Modules > 0 ? bar.Modules : Math.Max(20, payload.Length * 11);
            int module = boxdots / modules;
            if (module < 2) module = 2;
            if (module > 6) module = 6;
            int height = (int)Math.Round((double)bar.Height * ESCPOS_DPI / Twips.TWIPS_PER_INCH);
            if (height < 1) height = 1;
            if (height > 255) height = 255;

            using (System.IO.MemoryStream s = new System.IO.MemoryStream())
            {
                s.Write(new byte[] { 27, (byte)'a', align }, 0, 3);                       // ESC a n
                s.Write(new byte[] { 29, (byte)'h', (byte)height }, 0, 3);                 // GS h n: bar height
                s.Write(new byte[] { 29, (byte)'w', (byte)module }, 0, 3);                 // GS w n: module width
                s.Write(new byte[] { 29, (byte)'H', 0 }, 0, 3);                            // GS H 0: no HRI
                s.Write(new byte[] { 29, (byte)'k', m, (byte)bytes.Length }, 0, 4);        // GS k m n d1..dn
                s.Write(bytes, 0, bytes.Length);
                s.Write(new byte[] { 27, (byte)'a', 0 }, 0, 3);
                return s.ToArray();
            }
        }
        /// <summary>
        /// PDF417 through <c>GS ( k</c> cn 48: columns and rows as the item asked (0 = automatic), the
        /// module width from the box, the row height as three modules, the error correction level
        /// (0-8, carried in Flags bits 8-15), truncated when asked, then store and print.
        /// </summary>
        public static byte[] EncodeEscPosPdf417(string data, MetaObjectBarcode bar, byte align)
        {
            byte[] payload = Encoding.GetEncoding(28591).GetBytes(data ?? "");
            int boxdots = (int)Math.Round((double)bar.Width * ESCPOS_DPI / Twips.TWIPS_PER_INCH);
            int modules = bar.Modules > 0 ? bar.Modules : 100;
            int module = boxdots / modules;
            if (module < 2) module = 2;
            if (module > 8) module = 8;
            int columns = bar.Columns < 0 ? 0 : Math.Min(30, bar.Columns);
            int rows = bar.Rows < 3 ? 0 : Math.Min(90, bar.Rows);
            int ecc = (bar.Flags >> 8) & 0xFF;
            if (ecc > 8) ecc = 8;
            byte options = (byte)((bar.Flags & 1) != 0 ? 1 : 0);
            int k = payload.Length + 3;
            using (System.IO.MemoryStream s = new System.IO.MemoryStream())
            {
                s.Write(new byte[] { 27, (byte)'a', align }, 0, 3);
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 65, (byte)columns }, 0, 8);          // fn 65: columns
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 66, (byte)rows }, 0, 8);             // fn 66: rows
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 67, (byte)module }, 0, 8);           // fn 67: module width
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 68, (byte)Math.Min(8, module * 3) }, 0, 8); // fn 68: row height
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 4, 0, 48, 69, 48, (byte)(48 + ecc) }, 0, 9);    // fn 69: error correction level
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 70, options }, 0, 8);                // fn 70: options
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', (byte)(k & 0xFF), (byte)(k >> 8), 48, 80, 48 }, 0, 8); // fn 80: store
                s.Write(payload, 0, payload.Length);
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 81, 48 }, 0, 8);                     // fn 81: print
                s.Write(new byte[] { 27, (byte)'a', 0 }, 0, 3);
                return s.ToArray();
            }
        }
        /// <summary>
        /// The ESC/POS QR sequence: model 2, module size chosen so the symbol fills the box the
        /// layout reserved (the same grid the drawn version spreads over: modules across the box,
        /// quiet zone included), error correction as the item asked, data stored and printed. The
        /// alignment is set before and restored to left after, because ESC/POS keeps it for the
        /// following lines and the text lines are laid out from column 0.
        /// </summary>
        /// <param name="data">Data to encode.</param>
        /// <param name="bar">Barcode object, for the box and the parameters.</param>
        /// <param name="align">0 left, 1 centre, 2 right.</param>
        public static byte[] EncodeEscPosQr(string data, MetaObjectBarcode bar, byte align)
        {
            // Module size in dots: the box width in dots over the modules the encoder said it
            // needs. Clamped to what the command admits (1..16); 3 is the smallest a phone reads
            // reliably on thermal paper, so it is the floor even for a box that would ask for less.
            int boxdots = (int)Math.Round((double)bar.Width * ESCPOS_DPI / Twips.TWIPS_PER_INCH);
            int modules = bar.Modules > 0 ? bar.Modules : 33;
            int module = boxdots / modules;
            if (module < 3)
                module = 3;
            if (module > 16)
                module = 16;
            byte ecc;
            switch (bar.Ecc)
            {
                case MetaBarcodeEcc.M: ecc = 49; break;
                case MetaBarcodeEcc.Q: ecc = 50; break;
                case MetaBarcodeEcc.H: ecc = 51; break;
                default: ecc = 48; break;
            }
            // The data goes as the bytes a reader will assume without an ECI: ISO-8859-1. A URL is
            // ASCII anyway; anything outside Latin-1 becomes '?', which is what the printer would
            // have done with a byte it cannot map.
            byte[] payload = Encoding.GetEncoding(28591).GetBytes(data);
            int k = payload.Length + 3;

            using (System.IO.MemoryStream s = new System.IO.MemoryStream())
            {
                s.Write(new byte[] { 27, (byte)'a', align }, 0, 3);                              // ESC a n
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 4, 0, 49, 65, 50, 0 }, 0, 9);      // fn 65: model 2
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 49, 67, (byte)module }, 0, 8); // fn 67: module size
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 49, 69, ecc }, 0, 8);          // fn 69: error correction
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', (byte)(k & 0xFF), (byte)(k >> 8), 49, 80, 48 }, 0, 8); // fn 80: store
                s.Write(payload, 0, payload.Length);
                s.Write(new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 49, 81, 48 }, 0, 8);           // fn 81: print
                s.Write(new byte[] { 27, (byte)'a', 0 }, 0, 3);                                  // back to left
                return s.ToArray();
            }
        }
        /// <summary>
        /// Lays out and emits a block of text inside a rectangle, honouring alignment, clipping,
        /// word wrapping, justification and right-to-left ordering, and writes each resulting line
        /// through <see cref="DoTextOut"/>.
        /// </summary>
        /// <param name="arect">Rectangle, in twips, into which the text is placed.</param>
        /// <param name="text">Text to render.</param>
        /// <param name="alignment">Combination of MetaFile alignment flags.</param>
        /// <param name="clipping">When true, lines that fall outside the rectangle are clipped.</param>
        /// <param name="wordbreak">When true, text is wrapped on word boundaries.</param>
        /// <param name="righttoleft">When true, characters are ordered right-to-left.</param>
        /// <param name="fontstep">Character-per-inch step used to measure the text.</param>
        /// <param name="fontstyle">Bit mask of font style flags (bold, italic, underline, strikeout).</param>
        /// <param name="red">When true, the text is emitted in the red colour.</param>
        public void TextRect(Rectangle arect, string text, int alignment, bool clipping,
            bool wordbreak, bool righttoleft, PrintStepType fontstep, int fontstyle, bool red)
        {
            text = text.Replace("" + (char)13 + (char)10, "" + (char)10);
            Rectangle recsize;
            int posx, posy;
            bool singleline;
            string astring = "";
            Strings lwords, lwidths;
            int alinesize, alinedif, currpos;
            Rectangle arec;

            singleline = (alignment & MetaFile.AlignmentFlags_SingleLine) > 0;
            if (singleline)
                wordbreak = false;
            // The step the printer will actually use. CalculateTextExtent already measures with
            // it, but the lines were being STORED with the requested step: a 16 pt "TOTAL" asked
            // for 5 cpi, was measured at the 6 cpi the TM-88 has, and then the line carried
            // cpi5 — for which the receipt drivers have no escape (plain font A) — so the text
            // was placed on a 15-column line and printed 12 columns wide, truncated. Resolving
            // it once here keeps measure, columns and escape on the same step.
            fontstep = NearestFontStep(fontstep);
            recsize = arect;
            CalculateTextExtent(text, ref recsize, wordbreak, singleline, fontstep, clipping);
            posy = arect.Top;
            if ((alignment & MetaFile.AlignmentFlags_AlignBottom) > 0)
                posy = arect.Bottom - recsize.Height;
            if ((alignment & MetaFile.AlignmentFlags_AlignVCenter) > 0)
                posy = arect.Top + (((arect.Bottom - arect.Top) - recsize.Height) / 2);
            for (int i = 0; i < linfos.Count; i++)
            {
                posx = arect.Left;
                LineInfo linfo = linfos[i];
                if ((alignment & MetaFile.AlignmentFlags_AlignRight) > 0)
                    posx = arect.Right - linfo.Width;
                // Align horz
                if ((alignment & MetaFile.AlignmentFlags_AlignHCenter) > 0)
                    posx = arect.Left + (((arect.Right - arect.Left) - linfo.Width) / 2);
                if (linfo.Size <= 0)
                    astring = "";
                else
                    astring = text.Substring(linfo.Position, linfo.Size);
                // Justify
                if ((alignment & MetaFile.AlignmentFlags_AlignHJustify) > 0)
                {
                    lwords = Strings.FromSeparator(' ', astring);
                    lwords.RemoveBlanks();
                    alinesize = 0;
                    lwidths = new Strings();
                    foreach (string nword in lwords)
                    {
                        arec = new Rectangle(0, arect.Top, (int)Math.Round(nword.Length * StepToTwips(fontstep)), arect.Height);
                        if (righttoleft)
                            lwidths.Add((-(arec.Right - arec.Left)).ToString());
                        else
                            lwidths.Add((arec.Right - arec.Left).ToString());
                        alinesize = alinesize + (arec.Right - arec.Left);
                    }
                    alinedif = arect.Right - arect.Left - alinesize;
                    if (alinedif > 0)
                    {
                        if (lwords.Count > 1)
                            alinedif = alinedif / (lwords.Count - 1);
                        if (righttoleft)
                        {
                            currpos = arect.Right;
                            alinedif = -alinedif;
                        }
                        else
                            currpos = posx;
                        int idx = 0;
                        foreach (string nword in lwords)
                        {
                            DoTextOut(currpos, posx + linfo.TopPos, nword,
                                linfo.Width, fontstep, righttoleft, fontstyle, red);
                            currpos = currpos + System.Convert.ToInt32(lwidths[idx]) + alinedif;

                            idx++;
                        }
                    }
                    else
                        DoTextOut(posx, posy + linfo.TopPos, astring, linfo.Width, fontstep, righttoleft, fontstyle, red);
                }
                else
                    DoTextOut(posx, posy + linfo.TopPos, astring, linfo.Width, fontstep, righttoleft, fontstyle, red);

            }
        }
        /// <summary>
        /// Converts a vertical position in twips into the zero-based index of the corresponding print
        /// line, clamped to the available line range.
        /// </summary>
        /// <param name="posy">Vertical position in twips.</param>
        /// <returns>The line index for the given position.</returns>
        public int GetLineIndex(int posy)
        {
            int amax;
            int nresult = 0;
            if (FPageHeight <= 0)
                return nresult;
            amax = Lines.Count;
            // Lines go on base 0 that is from 0 to 65 in a 66 line paper.
            //
            // The position is divided by the LINE PITCH (1440 twips over the lines per inch),
            // not by page height over line count: the count is rounded, so on a page that is
            // not a whole number of lines (a 40 cm roll at 6 lpi is 94.5 lines) the old
            // quotient stretched every line by a fraction and rows laid out exactly one line
            // apart drifted onto each other. And halves round AWAY from zero: with banker's
            // rounding two rows sitting at k+.5 and k+1.5 both landed on the even line.
            double pitch = (double)Twips.TWIPS_PER_INCH / ((double)FLinesPerInch / 100.0);
            nresult = (int)Math.Round(System.Convert.ToDouble(posy) / pitch, MidpointRounding.AwayFromZero);
            if (nresult < 0)
                nresult = 0;
            if (nresult > amax - 1)
                nresult = amax - 1;
            return nresult;
        }
        /// <summary>
        /// Converts a horizontal position in twips into a column number for the given font step.
        /// </summary>
        /// <param name="posx">Horizontal position in twips.</param>
        /// <param name="fontstep">Character-per-inch step used to size a column.</param>
        /// <returns>The zero-based column number for the given position.</returns>
        public int GetColumnNumber(int posx, PrintStepType fontstep)
        {
            int nresult = (int)Math.Round(System.Convert.ToDouble(posx) / StepToTwips(fontstep));
            if (nresult < 0)
                nresult = 0;
            return nresult;
        }
        /// <summary>
        /// Builds a blank line of spaces wide enough to span the page at the given font step.
        /// </summary>
        /// <param name="fontstep">Character-per-inch step that determines the character count.</param>
        /// <returns>A string of spaces covering the full page width.</returns>
        public string GetBlankLine(PrintStepType fontstep)
        {
            StringBuilder nresult = new StringBuilder();
            int charcount = (int)Math.Round(System.Convert.ToDouble(FPageWidth) / StepToTwips(fontstep));
            for (int i = 0; i < charcount; i++)
                nresult.Append(" ");
            return nresult.ToString();
        }
        /// <summary>
        /// Places a text fragment at the given position on the appropriate print line, merging it with
        /// existing content and recording its style attributes.
        /// </summary>
        /// <param name="x">Horizontal position in twips.</param>
        /// <param name="y">Vertical position in twips, used to select the target line.</param>
        /// <param name="text">Text fragment to place.</param>
        /// <param name="linewidth">Width of the source line in twips.</param>
        /// <param name="fontstep">Character-per-inch step for the fragment.</param>
        /// <param name="rightoleft">When true, the fragment is reversed for right-to-left output.</param>
        /// <param name="fontstyle">Bit mask of font style flags (bold, italic, underline, strikeout).</param>
        /// <param name="red">When true, the fragment is marked to print in red.</param>
        public void DoTextOut(int x, int y, string text, int linewidth, PrintStepType fontstep, bool rightoleft, int fontstyle, bool red)
        {
            string astring;
            int atpos;
            int lineindex, index;
            int columnnumber;
            int toposition;

            astring = text;
            if (rightoleft)
                astring = PDFCanvas.DoReverseString(astring);
            lineindex = GetLineIndex(y);
            PrintLine nline = Lines[lineindex];
            if (nline.texts.Count < 1)
            {
                nline.FontStep = fontstep;
                nline.Value = GetBlankLine(fontstep);
            }
            PosText npostext = new PosText();
            columnnumber = GetColumnNumber(x, nline.FontStep);
            atpos = columnnumber;
            index = nline.texts.IndexOfKey(atpos);
            bool doinsert = false;
            if (index < 0)
            {
                npostext = new PosText();
                npostext.bold = ((fontstyle & 1) > 0);
                npostext.underline = ((fontstyle & 4) > 0);
                npostext.italic = ((fontstyle & 2) > 0);
                npostext.strokeout = ((fontstyle & 8) > 0);
                npostext.red = red;
                npostext.regular = (!npostext.bold) && (!npostext.strokeout) && (!npostext.italic) &&
                    (!npostext.strokeout);
                npostext.Position = columnnumber;
                doinsert = true;
            }
            else
            {
                npostext = nline.texts[atpos];
            }
            toposition = astring.Length;
            // Trunc strings. A fragment that ENDS at the last column fits: the old test used
            // ">=" and "- 1", so an amount right-aligned to the page edge always lost its
            // last digit ("34,9" for 34,90).
            if (columnnumber + toposition > nline.Value.Length)
                toposition = nline.Value.Length - columnnumber;
            if (toposition < 0)
                toposition = 0;
            npostext.nSize = toposition;
            string oldvalue = nline.Value;
            nline.Value = "";
            if (columnnumber > 0)
            {
                if (columnnumber > oldvalue.Length)
                {
                    nline.Value = oldvalue;
                    return;
                }
                else
                    nline.Value = oldvalue.Substring(0, columnnumber);
            }
            nline.Value = nline.Value + astring.Substring(0, toposition);
            if (nline.Value.Length < oldvalue.Length)
                nline.Value = nline.Value + oldvalue.Substring(nline.Value.Length, oldvalue.Length - nline.Value.Length);
            if (doinsert)
                nline.texts.Add(atpos, npostext);
            else
                nline.texts[atpos] = npostext;
        }
        /// <summary>
        /// The driver should do initialization here, a print driver should start a print document,
        /// while a preview driver should initialize a bitmap
        /// </summary>
        public override void NewDocument(MetaFile meta)
        {
            PrintResultStream = new System.IO.MemoryStream();
            whitelines = 0;
            blacklines = 0;
            PreviousLine = null;
            FLinesPerInch = meta.LinesPerInch;
            FDrawerBefore = meta.OpenDrawerBefore;
            FDrawerAfter = meta.OpenDrawerAfter;
            FPageWidth = meta.CustomX;
            FPageHeight = meta.CustomY;
            FResultStream = new System.IO.MemoryStream();
            FPrinterSelect = meta.PrinterSelect;
            UpdatePrinterConfig();

            RecalcSize();

        }
        /// <summary>
        /// Controls whether OEM character-set conversion is applied to output text: use the printer
        /// configuration default (<c>None</c>), or force it off (<c>False</c>) or on (<c>True</c>).
        /// </summary>
        public enum OemConvertOverride {
            /// <summary>Use the OEM conversion setting from the printer configuration.</summary>
            None,
            /// <summary>Force OEM character-set conversion off.</summary>
            False,
            /// <summary>Force OEM character-set conversion on.</summary>
            True
        };

        /// <summary>
        /// Selects whether OEM character-set conversion follows the printer configuration or is forced
        /// on or off for this driver instance.
        /// </summary>
        public OemConvertOverride OverrideOemConvert = OemConvertOverride.None;

        private void UpdatePrinterConfig()
        {
            if (ForceDriverName.Length == 0)
            {
                FPrinterDriver = PrinterConfig.GetDriverName(FPrinterSelect);
            }
            else
                FPrinterDriver = ForceDriverName;
            if (OverrideOemConvert != OemConvertOverride.None)
            {
                if (OverrideOemConvert == OemConvertOverride.False)
                    OEMConvert = false;
                else
                    OEMConvert = true;
            }
            else
            if (LoadOEMConvert)
            {
                OEMConvert = PrinterConfig.GetOEMConvert(FPrinterSelect);
            }

            allowedsteps = new SortedList<PrintStepType, PrintStepType>
            {
                { PrintStepType.cpi5, PrintStepType.cpi10 },
                { PrintStepType.cpi6, PrintStepType.cpi10 },
                { PrintStepType.cpi10, PrintStepType.cpi10 },
                { PrintStepType.cpi12, PrintStepType.cpi10 },
                { PrintStepType.cpi15, PrintStepType.cpi10 },
                { PrintStepType.cpi17, PrintStepType.cpi10 },
                { PrintStepType.cpi20, PrintStepType.cpi10 }
            };

            FillEscapes();

        }
        /// <summary>
        /// Rebuilds the escape-code table and the allowed font-step map for the active printer driver,
        /// defining the control sequences used to render text.
        /// </summary>
        /// <summary>
        /// The code page a driver FORCES for its text (0 = none: the OEMConvert setting decides,
        /// which is what the legacy drivers do). Set by <see cref="FillEscapes"/> for the drivers
        /// that declare their character set to the printer.
        /// </summary>
        public int DriverCodePage;
        /// <summary>The TM88 receipt drivers share everything but the init and the cut.</summary>
        private void FillTm88(bool cut, byte[] init, int codepage)
        {
            escapecodes[PrinterRawOperation.InitPrinter] = init;
            escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
            escapecodes[PrinterRawOperation.Linespacen_60] = new byte[] { 27, 3 };
            escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
            escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
            masterselect = true;
            limitedmaster = true;
            condensedmaster = true;
            // ESC m (partial cut) cuts right where the head is: the last ~15 mm of the receipt (the paper between the
            // print head and the cutter) stayed inside and only came out with the next ticket ("el ticket no sale
            // completo", Roberto, 2026-09-09). Same ending as the raster/HTML ticket path (escpos.ts): four line feeds
            // and GS V 66 0 (feed to the cutting position, then partial cut), followed by the reset.
            escapecodes[PrinterRawOperation.EndPrint] = cut ? new byte[] { 27, (byte)'d', 4, 29, (byte)'V', 66, 0, 27, 64 } : new byte[] { 27, 64 };
            escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
            DriverCodePage = codepage;
        }
        /// <summary>The Epson TM88 family (ESC/POS receipt printers), in all its variants.</summary>
        public static bool IsTm88Driver(string drivername)
        {
            return drivername == "EPSONTM88II" || drivername == "EPSONTM88IICUT"
                || drivername == "EPSONTM88CP850" || drivername == "EPSONTM88CP850CUT"
                || drivername == "EPSONTM88CP858" || drivername == "EPSONTM88CP858CUT"
                || drivername == "EPSONTM88UTF8" || drivername == "EPSONTM88UTF8CUT";
        }
        /// <summary>The receipt drivers whose pages are cut where the text ends (no padding to the page length).</summary>
        public static bool IsReceiptDriver(string drivername)
        {
            return IsTm88Driver(drivername) || drivername == "EPSONTMU210" || drivername == "EPSONTMU210CUT";
        }
        public void FillEscapes()
        {
            Type rtype = typeof(PrinterRawOperation);


            escapecodes.Clear();
            DriverCodePage = 0;
            foreach (string s in Enum.GetNames(rtype))
            {
                escapecodes.Add((PrinterRawOperation)Enum.Parse(rtype, s), emptyByteArray);
            }
            masterselect = false;
            limitedmaster = false;
            condensedmaster = false;

            switch (FPrinterDriver)
            {
                case "EPSON":
                    // Init Printer-Line spacing to 1/6
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64 };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.LineSpace8] = new byte[] { 27, (byte)'0' };
                    escapecodes[PrinterRawOperation.LineSpace7_72] = new byte[] { 27, (byte)'1' };
                    escapecodes[PrinterRawOperation.LineSpacen_216] = new byte[] { 27, (byte)'3' };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    escapecodes[PrinterRawOperation.FF] = new byte[] { 12 };
                    escapecodes[PrinterRawOperation.Underline] = new byte[] { 27, 45, 1 };
                    // Underline off-Bold off-Italic off
                    escapecodes[PrinterRawOperation.Normal] = new byte[] { 27, 45, 0 };
                    // Set 10 or 12 cpi, enabled-disable double wide, enable-disable condensed
                    escapecodes[PrinterRawOperation.cpi5] = new byte[] { 18, 14 };
                    escapecodes[PrinterRawOperation.cpi10] = new byte[] { 20, 18 };
                    escapecodes[PrinterRawOperation.cpi17] = new byte[] { 20, 15 };
                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, 64 };


                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    break;
                case "EPSON-MASTER":
                    // Init Printer-Line spacing to 1/6 - Draft mode
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64, 27, (byte)'x', (byte)'0' };
                    //har)27+"["+"4"+"q";
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.LineSpace8] = new byte[] { 27, (byte)'0' };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    escapecodes[PrinterRawOperation.FF] = new byte[] { 12 };
                    masterselect = true;
                    limitedmaster = false;
                    condensedmaster = false;

                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, 64 };
                    break;
                case "EPSON-ESCP":
                    // Init Printer-Line spacing to 1/6 - Draft mode - Bidirectional print
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64, 27, (byte)'x', (byte)'1', 27, (byte)'U', (byte)0 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.LineSpace7_72] = new byte[] { 27, (byte)'1' };
                    escapecodes[PrinterRawOperation.LineSpace8] = new byte[] { 27, (byte)'0' };
                    escapecodes[PrinterRawOperation.LineSpacen_216] = new byte[] { 27, (byte)'3' };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { (byte)10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    escapecodes[PrinterRawOperation.FF] = new byte[] { 12 };
                    escapecodes[PrinterRawOperation.Bold] = new byte[] { 27, (byte)'E' };
                    escapecodes[PrinterRawOperation.Underline] = new byte[] { 27, 45, 1 };
                    escapecodes[PrinterRawOperation.Italic] = new byte[] { 27, (byte)'4' };
                    // Underline off-Bold off-Italic off
                    escapecodes[PrinterRawOperation.Normal] = new byte[] { 27, 45, 0, 27, (byte)'F', 27, (byte)'5' };
                    // Set 10 or 12 cpi, enabled-disable double wide, enable-disable condensed
                    escapecodes[PrinterRawOperation.cpi5] = new byte[] { 27, (byte)'P', 14, 18 };
                    escapecodes[PrinterRawOperation.cpi6] = new byte[] { 27, (byte)'M', 14, 18 };
                    escapecodes[PrinterRawOperation.cpi10] = new byte[] { 27, (byte)'P', 20, 18 };
                    escapecodes[PrinterRawOperation.cpi12] = new byte[] { 27, (byte)'M', 20, 18 };
                    // 15 cpi not supported in LX models
                    //  escapecodes[rpescape15cpi]=""+(char)27+"g"+(char)20;
                    escapecodes[PrinterRawOperation.cpi17] = new byte[] { 27, (byte)'P', 20, 15 };
                    escapecodes[PrinterRawOperation.cpi20] = new byte[] { 27, (byte)'M', 20, 15 };

                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, 64 };

                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    break;
                case "EPSON-ESCPQ":
                    // Init Printer-Line spacing to 1/6 - Draft mode - Bidirectional print
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64, 27, (byte)'x', (byte)'1', 27, (byte)'U', 0 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.LineSpace8] = new byte[] { 27, (byte)'0' };
                    escapecodes[PrinterRawOperation.LineSpacen_180] = new byte[] { 27, (byte)'3' };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    escapecodes[PrinterRawOperation.FF] = new byte[] { 12 };
                    escapecodes[PrinterRawOperation.Bold] = new byte[] { 27, (byte)'E' };
                    escapecodes[PrinterRawOperation.Underline] = new byte[] { 27, 45, 1 };
                    escapecodes[PrinterRawOperation.Italic] = new byte[] { 27, (byte)'4' };
                    // Underline off-Bold off-Italic off
                    escapecodes[PrinterRawOperation.Normal] = new byte[] { 27, 45, 0, 27, (byte)'F', 27, (byte)'5' };
                    // Set 10 or 12 cpi, enabled-disable double wide, enable-disable condensed
                    escapecodes[PrinterRawOperation.cpi5] = new byte[] { 27, (byte)'P', 14, 18 };
                    escapecodes[PrinterRawOperation.cpi6] = new byte[] { 27, (byte)'M', 14, 18 };
                    escapecodes[PrinterRawOperation.cpi10] = new byte[] { 27, (byte)'P', 20, 18 };
                    escapecodes[PrinterRawOperation.cpi12] = new byte[] { 27, (byte)'M', 20, 18 };
                    // 15 cpi not supported in LX models
                    //  escapecodes[rpescape15cpi]=""+(char)27+"g"+(char)20;
                    escapecodes[PrinterRawOperation.cpi17] = new byte[] { 27, (byte)'P', 20, 15 };
                    escapecodes[PrinterRawOperation.cpi20] = new byte[] { 27, (byte)'M', 20, 15 };

                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, 64 };

                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    break;
                case "IBMPROPRINTER":
                    // Init Printer-Line spacing to 1/6 - Draft mode
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 20, 20, 27, 64 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.LineSpace8] = new byte[] { 27, (byte)'0' };
                    escapecodes[PrinterRawOperation.LineSpace7_72] = new byte[] { 27, (byte)'1' };
                    escapecodes[PrinterRawOperation.LineSpacen_216] = new byte[] { 27, (byte)'3' };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    escapecodes[PrinterRawOperation.FF] = new byte[] { 12 };
                    escapecodes[PrinterRawOperation.Bold] = new byte[] { 27, (byte)'E' };
                    escapecodes[PrinterRawOperation.Underline] = new byte[] { 27, 45, 1 };
                    escapecodes[PrinterRawOperation.Italic] = new byte[] { 27, (byte)'4' };
                    // Underline off-Bold off-Italic off
                    escapecodes[PrinterRawOperation.Normal] = new byte[] { 27, 45, 0, 27, (byte)'F', 27, (byte)'5' };
                    // Set 10 or 12 cpi, enabled-disable double wide, enable-disable condensed
                    escapecodes[PrinterRawOperation.cpi5] = new byte[] { 27, 18, 14, 18 };
                    escapecodes[PrinterRawOperation.cpi6] = new byte[] { 27, (byte)':', 14, 18 };
                    escapecodes[PrinterRawOperation.cpi10] = new byte[] { 27, 18, 20, 18 };
                    escapecodes[PrinterRawOperation.cpi12] = new byte[] { 20, 18, 27, (byte)':' };
                    escapecodes[PrinterRawOperation.cpi17] = new byte[] { 20, 27, 18, 15 };
                    escapecodes[PrinterRawOperation.cpi20] = new byte[] { 20, 27, (byte)':', 15 };

                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 20, 20, 27, 64 };
                    break;
                case "EPSONTMU210":
                    // Init Printer-Line spacing to 1/6 - Draft mode
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.Linespacen_60] = new byte[] { 27, (byte)'3' };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    //escapecodes[PrinterRawOperation.FF]=""+(char)12;
                    masterselect = true;
                    limitedmaster = true;
                    condensedmaster = false;

                    // Can select red font
                    escapecodes[PrinterRawOperation.RedFont] = new byte[] { 27, (byte)'r', 1 };
                    escapecodes[PrinterRawOperation.BlackFont] = new byte[] { 27, (byte)'r', 0 };

                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, 64 };
                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    break;
                case "EPSONTMU210CUT":
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.Linespacen_60] = new byte[] { 27, (byte)'3' };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    //escapecodes[PrinterRawOperation.FF]=""+(char)12;
                    masterselect = true;
                    limitedmaster = true;
                    condensedmaster = false;
                    // Can select red font
                    escapecodes[PrinterRawOperation.RedFont] = new byte[] { 27, (byte)'r', 1 };
                    escapecodes[PrinterRawOperation.BlackFont] = new byte[] { 27, (byte)'r', 0 };
                    // Cut paper: feed to the cutting position first (ESC d 4 + GS V 66 0), see FillTm88.
                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, (byte)'d', 4, 29, (byte)'V', 66, 0, 27, 64 };
                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    break;
                case "EPSONTM88II":
                    // Init Printer-Line spacing to 1/6 - Draft mode
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.Linespacen_60] = new byte[] { 27, (byte)'3' };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    //escapecodes[PrinterRawOperation.FF]=""+(char)12;
                    masterselect = true;
                    limitedmaster = true;
                    condensedmaster = true;

                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, 64 };
                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    break;

                case "EPSONTM88IICUT":
                    // Init Printer-Line spacing to 1/6 - Draft mode
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64 };
                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, (byte)'2' };
                    escapecodes[PrinterRawOperation.Linespacen_60] = new byte[] { 27, 3 };
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    //escapecodes[PrinterRawOperation.FF]=""+(char)12;
                    masterselect = true;
                    limitedmaster = true;
                    condensedmaster = true;
                    // Cut paper: feed to the cutting position first (ESC d 4 + GS V 66 0), see FillTm88.
                    escapecodes[PrinterRawOperation.EndPrint] = new byte[] { 27, (byte)'d', 4, 29, (byte)'V', 66, 0, 27, 64 };
                    // Open drawer
                    escapecodes[PrinterRawOperation.Pulse] = new byte[] { 27, 112, 0, 100, 100 };
                    break;
                // THE TM88 FAMILY WITH THE CHARACTER SET SAID OUT LOUD. The two drivers above never
                // tell the printer which code page the bytes are in: this driver has always converted
                // to cp850 (OEMConvert), so a printer left at its factory table (an Epson: PC437)
                // paints the accents wrong until someone configures it. These variants keep the
                // legacy ones untouched and add the declaration to the init — ESC t 2 (PC850),
                // ESC t 19 (PC858, cp850 plus the euro at 0xD5) or FS ( C fn 48 m 2 (UTF-8, Epson
                // TM-T88IV+/T20/T70/T82/m30/m50/P20/P80…; with it ESC t is ignored) — and FORCE the
                // matching text encoding regardless of the OEMConvert setting.
                case "EPSONTM88CP850":
                    FillTm88(false, new byte[] { 27, 64, 27, (byte)'t', 2 }, 850);
                    break;
                case "EPSONTM88CP850CUT":
                    FillTm88(true, new byte[] { 27, 64, 27, (byte)'t', 2 }, 850);
                    break;
                case "EPSONTM88CP858":
                    FillTm88(false, new byte[] { 27, 64, 27, (byte)'t', 19 }, 858);
                    break;
                case "EPSONTM88CP858CUT":
                    FillTm88(true, new byte[] { 27, 64, 27, (byte)'t', 19 }, 858);
                    break;
                case "EPSONTM88UTF8":
                    FillTm88(false, new byte[] { 27, 64, 28, (byte)'(', (byte)'C', 2, 0, 48, 2 }, 65001);
                    break;
                case "EPSONTM88UTF8CUT":
                    FillTm88(true, new byte[] { 27, 64, 28, (byte)'(', (byte)'C', 2, 0, 48, 2 }, 65001);
                    break;
                case "HP-PCL":
                    // Init printer + 6 lines per inch
                    escapecodes[PrinterRawOperation.InitPrinter] = new byte[] { 27, 64 };

                    escapecodes[PrinterRawOperation.LineSpace6] = new byte[] { 27, 38, 108, 54, 68 };
                    escapecodes[PrinterRawOperation.LineSpace8] = new byte[] { 27, 38, 108, 56, 68 };

                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    escapecodes[PrinterRawOperation.FF] = new byte[] { 27, 38, 108, 48, 72 }; // Form feed and eject page
                    escapecodes[PrinterRawOperation.Bold] = new byte[] { 27, 40, 115, 51, 66 };
                    escapecodes[PrinterRawOperation.Underline] = new byte[] { 27, 38, 100, 48, 68 };
                    escapecodes[PrinterRawOperation.Italic] = new byte[] { 27, 40, 115, 49, 83 };
                    // Underline off-Bold off-Italic off
                    escapecodes[PrinterRawOperation.Normal] = new byte[] {27, 38, 100, 64,
                      27,40, 115, 48 , 66 , 27, 40, 115, 48, 83 };
                    // Set 10 or 12 cpi, enabled-disable double wide, enable-disable condensed
                    escapecodes[PrinterRawOperation.cpi5] = new byte[] { 27, 40, 115, 5, 72 };
                    escapecodes[PrinterRawOperation.cpi6] = new byte[] { 27, 40, 115, 6, 72 };
                    escapecodes[PrinterRawOperation.cpi10] = new byte[] { 27, 38, 107, 48, 83 };
                    escapecodes[PrinterRawOperation.cpi15] = new byte[] { 27, 38, 107, 52, 83 };
                    escapecodes[PrinterRawOperation.cpi17] = new byte[] { 27, 40, 115, 17, 72 };
                    escapecodes[PrinterRawOperation.cpi20] = new byte[] { 27, 40, 115, 20, 72 };
                    break;
                case "PLAIN":
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    break;
                case "PLAINFULL":
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    escapecodes[PrinterRawOperation.CR] = new byte[] { 13 };
                    //                    FFullPlain = true;
                    break;
                case "VT100":
                    // Init Printer-Line spacing to 1/6 - Draft mode
                    //  escapecodes[PrinterRawOperation.InitPrinter]=""+(char)27+(char)64+(char)27+"2"+(char)27+"x"+(char)0;
                    escapecodes[PrinterRawOperation.LineFeed] = new byte[] { 10 };
                    //  escapecodes[PrinterRawOperation.CR]=""+(char)13;
                    //  escapecodes[PrinterRawOperation.FF]=""+(char)12;
                    //  escapecodes[rpescapenormal]=""+(char)27+"[0m";
                    // Set 10 or 12 cpi, enabled-disable double wide, enable-disable condensed
                    escapecodes[PrinterRawOperation.cpi15] = new byte[] { 27, (byte)'(', (byte)'c', (byte)'h', (byte)'a', (byte)'r', (byte)')', (byte)'6' };
                    escapecodes[PrinterRawOperation.cpi10] = new byte[] { 27, (byte)'(', (byte)'c', (byte)'h', (byte)'a', (byte)'r', (byte)')', (byte)'5' };
                    break;
            }
            if (escapecodes[PrinterRawOperation.cpi5].Length > 0)
                allowedsteps[PrintStepType.cpi5] = PrintStepType.cpi5;
            if (escapecodes[PrinterRawOperation.cpi6].Length > 0)
                allowedsteps[PrintStepType.cpi6] = PrintStepType.cpi6;
            if (escapecodes[PrinterRawOperation.cpi12].Length > 0)
                allowedsteps[PrintStepType.cpi12] = PrintStepType.cpi12;
            if (escapecodes[PrinterRawOperation.cpi15].Length > 0)
                allowedsteps[PrintStepType.cpi15] = PrintStepType.cpi15;
            if (escapecodes[PrinterRawOperation.cpi17].Length > 0)
                allowedsteps[PrintStepType.cpi17] = PrintStepType.cpi17;
            if (escapecodes[PrinterRawOperation.cpi20].Length > 0)
                allowedsteps[PrintStepType.cpi20] = PrintStepType.cpi20;
            if (masterselect)
            {
                allowedsteps[PrintStepType.cpi15] = PrintStepType.cpi17;
                if (limitedmaster)
                {
                    if (condensedmaster)
                    {
                        allowedsteps[PrintStepType.cpi10] = PrintStepType.cpi12;
                        allowedsteps[PrintStepType.cpi12] = PrintStepType.cpi12;
                        allowedsteps[PrintStepType.cpi5] = PrintStepType.cpi6;
                        allowedsteps[PrintStepType.cpi6] = PrintStepType.cpi6;
                        allowedsteps[PrintStepType.cpi20] = PrintStepType.cpi17;
                        allowedsteps[PrintStepType.cpi17] = PrintStepType.cpi17;
                    }
                    else
                    {
                        allowedsteps[PrintStepType.cpi15] = PrintStepType.cpi12;
                        allowedsteps[PrintStepType.cpi17] = PrintStepType.cpi12;
                        allowedsteps[PrintStepType.cpi20] = PrintStepType.cpi12;
                        allowedsteps[PrintStepType.cpi12] = PrintStepType.cpi12;
                    }
                }

            }
            else
            {
                if (escapecodes[PrinterRawOperation.cpi5].Length == 0)
                {
                    if (escapecodes[PrinterRawOperation.cpi6].Length > 0)
                        allowedsteps[PrintStepType.cpi5] = PrintStepType.cpi6;
                    else
                        allowedsteps[PrintStepType.cpi5] = PrintStepType.cpi10;
                }
                if (escapecodes[PrinterRawOperation.cpi6].Length == 0)
                {
                    allowedsteps[PrintStepType.cpi6] = PrintStepType.cpi10;
                }
                if (escapecodes[PrinterRawOperation.cpi12].Length == 0)
                {
                    if (escapecodes[PrinterRawOperation.cpi15].Length > 0)
                        allowedsteps[PrintStepType.cpi12] = PrintStepType.cpi15;
                    else
                    {
                        if (escapecodes[PrinterRawOperation.cpi17].Length > 0)
                            allowedsteps[PrintStepType.cpi12] = PrintStepType.cpi17;
                        else
                        {
                            if (escapecodes[PrinterRawOperation.cpi20].Length > 0)
                                allowedsteps[PrintStepType.cpi12] = PrintStepType.cpi20;
                            else
                                allowedsteps[PrintStepType.cpi12] = PrintStepType.cpi10;
                        }
                    }
                }
                if (escapecodes[PrinterRawOperation.cpi15].Length == 0)
                {
                    if (escapecodes[PrinterRawOperation.cpi17].Length > 0)
                        allowedsteps[PrintStepType.cpi15] = PrintStepType.cpi17;
                    else
                    {
                        if (escapecodes[PrinterRawOperation.cpi20].Length > 0)
                            allowedsteps[PrintStepType.cpi15] = PrintStepType.cpi20;
                        else
                            allowedsteps[PrintStepType.cpi15] = PrintStepType.cpi10;
                    }
                }
                if (escapecodes[PrinterRawOperation.cpi17].Length == 0)
                {
                    if (escapecodes[PrinterRawOperation.cpi20].Length > 0)
                        allowedsteps[PrintStepType.cpi17] = PrintStepType.cpi20;
                    else
                    {
                        if (escapecodes[PrinterRawOperation.cpi15].Length > 0)
                            allowedsteps[PrintStepType.cpi17] = PrintStepType.cpi15;
                        else
                            if (escapecodes[PrinterRawOperation.cpi12].Length > 0)
                            allowedsteps[PrintStepType.cpi17] = PrintStepType.cpi12;
                        else
                            allowedsteps[PrintStepType.cpi17] = PrintStepType.cpi10;
                    }
                }
                if (escapecodes[PrinterRawOperation.cpi20].Length == 0)
                {
                    if (escapecodes[PrinterRawOperation.cpi17].Length > 0)
                        allowedsteps[PrintStepType.cpi20] = PrintStepType.cpi17;
                    else
                    {
                        if (escapecodes[PrinterRawOperation.cpi15].Length > 0)
                            allowedsteps[PrintStepType.cpi20] = PrintStepType.cpi15;
                        else
                            if (escapecodes[PrinterRawOperation.cpi12].Length > 0)
                            allowedsteps[PrintStepType.cpi20] = PrintStepType.cpi12;
                        else
                            allowedsteps[PrintStepType.cpi20] = PrintStepType.cpi10;
                    }
                }
            }
        }

        /// <summary>
        /// Finalizes the document, emitting the open-drawer, cut-paper and related end-of-print
        /// operations configured for the selected printer.
        /// </summary>
        /// <param name="meta">MetaFile being finished.</param>
        public override void EndDocument(MetaFile meta)
        {
            byte[] init;
            // The driver's end-of-print escape and the tear-off, in this order and BEFORE the
            // drawer, as the Delphi driver does (rptextdriver.pas, EndDocument): on the CUT
            // receipt drivers EndPrint is the paper cut followed by a reset, so the ticket is cut
            // and the printer clean before the drawer pulse. The port used to emit neither, and
            // a receipt on an EPSONTM88IICUT was never cut unless the printer configuration
            // said so on its own.
            init = GetEscape(PrinterRawOperation.EndPrint);
            if (init.Length > 0)
                PrintResultStream.Write(init, 0, init.Length);
            init = GetEscape(PrinterRawOperation.TearOff);
            if (init.Length > 0)
                PrintResultStream.Write(init, 0, init.Length);
            if (FDrawerAfter)
            {
                init = GetEscape(PrinterRawOperation.Pulse);
                if (init.Length > 0)
                    PrintResultStream.Write(init, 0, init.Length);
            }
            if (Reportman.Drawing.PrinterConfig.GetCutPaperOption(FPrinterSelect))
            {
                init = Reportman.Drawing.PrinterConfig.GetCutPaperOperation(FPrinterSelect);
                if (init.Length == 0)
                    init = GetEscape(PrinterRawOperation.CutPaper);
                if (init.Length > 0)
                {
                    if (init.Length > 0)
                        PrintResultStream.Write(init, 0, init.Length);
                }
            }
            if (Reportman.Drawing.PrinterConfig.GetOpenDrawerOption(FPrinterSelect))
            {
                init = Reportman.Drawing.PrinterConfig.GetOpenDrawerOperation(FPrinterSelect);
                if (init.Length == 0)
                    init = GetEscape(PrinterRawOperation.OpenDrawer);
                if (init.Length > 0)
                {
                    init = GetEscape(PrinterRawOperation.OpenDrawer);
                    if (init.Length > 0)
                        PrintResultStream.Write(init, 0, init.Length);
                }
            }
        }
        /// <summary>
        /// Encodes every line of the current page into the page stream, optionally dropping trailing
        /// blank lines and appending a form feed or line feeds to advance to the next page.
        /// </summary>
        /// <param name="cutclearlines">When true, trailing empty lines are trimmed instead of padded.</param>
        public void WriteCurrentPage(bool cutclearlines)
        {
            int lastline = Lines.Count - 1;
            if (cutclearlines)
            {
                while (lastline > 0)
                {
                    // A line with a native symbol attached is not blank, whatever its text says.
                    if (Lines[lastline].texts.Count == 0 && Lines[lastline].RawBefore == null)
                    {
                        lastline--;
                    }
                    else
                        break;
                }
            }
            for (int i = 0; i <= lastline; i++)
            {
                if (Lines[i].RawBefore != null)
                {
                    foreach (byte[] raw in Lines[i].RawBefore)
                        currpageStream.Write(raw, 0, raw.Length);
                }
                // The paper under a native symbol was already fed by the printer while drawing it:
                // an empty line in that span is not emitted (see PrintLine.Consumed).
                if (Lines[i].Consumed && Lines[i].texts.Count == 0)
                    continue;
                byte[] codedstring = EncodeLine(Lines[i], i, FullPlain);
                if (Lines[i].texts.Count == 0)
                {
                    whitelines++;
                }
                else
                    blacklines++;
                currpageStream.Write(codedstring, 0, codedstring.Length);
            }
            if ((lastline < Lines.Count - 1) && (!cutclearlines))
            {
                byte[] escapeff = GetEscape(PrinterRawOperation.FF);
                byte[] escapelf = GetEscape(PrinterRawOperation.LineFeed);
                if ((escapeff.Length > 0) && (lastline < Lines.Count - 1))
                {
                    currpageStream.Write(escapeff, 0, escapeff.Length);
                }
                else
                {
                    while (lastline < Lines.Count - 1)
                    {
                        lastline++;
                        currpageStream.Write(escapelf, 0, escapelf.Length);
                        linefeeds++;
                    }
                }
            }
        }
        /// <summary>
        /// Returns the escape sequence that selects the given character-per-inch step, using master
        /// select codes when the driver supports them or the per-step escape codes otherwise.
        /// </summary>
        /// <param name="nstep">Character-per-inch step to select.</param>
        /// <returns>The escape bytes that switch the printer to the requested step.</returns>
        public byte[] GetFontStepEscape(PrintStepType nstep)
        {
            if (masterselect)
            {
                byte aselect;
                byte condcode;
                byte[] nresult = emptyByteArray;
                if (limitedmaster)
                    condcode = 0;
                else
                    condcode = mastercond;
                aselect = 0;
                if (limitedmaster && condensedmaster)
                {
                    switch (nstep)
                    {
                        case PrintStepType.cpi20:
                            aselect = 0;
                            break;
                        case PrintStepType.cpi17:
                            aselect = master12;
                            break;
                        case PrintStepType.cpi15:
                            aselect = 0;
                            break;
                        case PrintStepType.cpi12:
                            aselect = master10;
                            break;
                        case PrintStepType.cpi10:
                            aselect = 0;
                            break;
                        case PrintStepType.cpi6:
                            aselect = (byte)(master10 | masterwide);
                            break;
                        case PrintStepType.cpi5:
                            aselect = 0;
                            break;
                    }
                }
                else
                {
                    switch (nstep)
                    {
                        case PrintStepType.cpi20:
                            aselect = (byte)(master12 | condcode);
                            break;
                        case PrintStepType.cpi17:
                            aselect = (byte)(master10 | condcode);
                            break;
                        case PrintStepType.cpi15:
                            break;
                        case PrintStepType.cpi12:
                            aselect = master12;
                            break;
                        case PrintStepType.cpi10:
                            aselect = master10;
                            break;
                        case PrintStepType.cpi6:
                            aselect = (byte)(master12 | masterwide);
                            break;
                        case PrintStepType.cpi5:
                            aselect = (byte)(master10 | masterwide);
                            break;
                    }

                }
                nresult = new byte[] { 27, (byte)'!', aselect };
                return nresult;
            }
            byte[] s = emptyByteArray;
            switch (nstep)
            {
                case PrintStepType.cpi5:
                    s = GetEscape(PrinterRawOperation.cpi5);
                    break;
                case PrintStepType.cpi6:
                    s = GetEscape(PrinterRawOperation.cpi6);
                    break;
                case PrintStepType.cpi10:
                    s = GetEscape(PrinterRawOperation.cpi10);
                    break;
                case PrintStepType.cpi12:
                    s = GetEscape(PrinterRawOperation.cpi12);
                    break;
                case PrintStepType.cpi15:
                    s = GetEscape(PrinterRawOperation.cpi15);
                    break;
                case PrintStepType.cpi17:
                    s = GetEscape(PrinterRawOperation.cpi17);
                    break;
                case PrintStepType.cpi20:
                    s = GetEscape(PrinterRawOperation.cpi20);
                    break;
            }
            return s;
        }
        /// <summary>
        /// Encodes a single print line into a byte sequence, emitting font-step changes, per-fragment
        /// style escapes, the text bytes (with optional OEM conversion) and the trailing carriage
        /// return and line feed.
        /// </summary>
        /// <param name="Line">Print line to encode.</param>
        /// <param name="index">Zero-based index of the line within the page.</param>
        /// <param name="plain">When true, style escapes are omitted and fragments are trimmed.</param>
        /// <returns>The encoded bytes for the line.</returns>
        public byte[] EncodeLine(PrintLine Line, int index, bool plain)
        {
            using (System.IO.MemoryStream nline = new System.IO.MemoryStream())
            {

                bool changestep = false;
                if (PreviousLine == null)
                    changestep = true;
                else
                    if (PreviousLine.FontStep != Line.FontStep)
                    changestep = true;
                if (changestep)
                {
                    byte[] fstep = GetFontStepEscape(Line.FontStep);
                    nline.Write(fstep, 0, fstep.Length);
                    PreviousLine = new PrintLine();
                    PreviousLine.FontStep = Line.FontStep;
                }
                int linepos = 0;
                foreach (PosText npos in Line.texts.Values)
                {
                    while (linepos < npos.Position)
                    {
                        nline.Write(spaceByteArray, 0, spaceByteArray.Length);
                        linepos++;
                    }
                    if (!npos.regular)
                    {
                        if (npos.underline)
                        {
                            byte[] nunderline = GetEscape(PrinterRawOperation.Underline);
                            nline.Write(nunderline, 0, nunderline.Length);
                        }
                        if (npos.bold)
                        {
                            byte[] newScape = GetEscape(PrinterRawOperation.Bold);
                            nline.Write(newScape, 0, newScape.Length);
                        }
                        if (npos.red)
                        {
                            byte[] newScape = GetEscape(PrinterRawOperation.RedFont);
                            nline.Write(newScape, 0, newScape.Length);
                        }
                        if (npos.strokeout)
                        {
                            byte[] newScape = GetEscape(PrinterRawOperation.StrikeOut);
                            nline.Write(newScape, 0, newScape.Length);
                        }
                        if (npos.italic)
                        {
                            byte[] newScape = GetEscape(PrinterRawOperation.Italic);
                            nline.Write(newScape, 0, newScape.Length);
                        }
                    }
                    string newstring = Line.Value.Substring(npos.Position, npos.nSize);
                    if (plain)
                    {
                        newstring = newstring.Trim();
                    }
                    byte[] nbytes;
                    Encoding oriencoding = Encoding.UTF8;
                    if (DriverCodePage > 0)
                    {
                        // The driver declared its character set to the printer: the text goes in it.
                        nbytes = Encoding.GetEncoding(DriverCodePage).GetBytes(newstring);
                    }
                    else if (OEMConvert)
                    {
                        Encoding nencode = Encoding.GetEncoding(850);
                        nbytes = Encoding.Convert(oriencoding, nencode, oriencoding.GetBytes(newstring));
                    }
                    else
                    {
                        nbytes = oriencoding.GetBytes(newstring);
                    }
                    nline.Write(nbytes, 0, nbytes.Length);
                    linepos = linepos + nbytes.Length;
                    if (!npos.regular && !plain)
                    {
                        byte[] newScape = GetEscape(PrinterRawOperation.Normal);
                        nline.Write(newScape, 0, newScape.Length);
                        if (npos.red)
                        {
                            newScape = GetEscape(PrinterRawOperation.BlackFont);
                            nline.Write(newScape, 0, newScape.Length);
                        }
                    }
                }
                byte[] escapcr = GetEscape(PrinterRawOperation.CR);
                nline.Write(escapcr, 0, escapcr.Length);
                byte[] escapeff = GetEscape(PrinterRawOperation.FF);
                byte[] escapelf = GetEscape(PrinterRawOperation.LineFeed);
                if (escapeff.Length > 0)
                {
                    if (index == Lines.Count - 1)
                        nline.Write(escapelf, 0, escapelf.Length);
                    else
                        nline.Write(escapelf, 0, escapelf.Length);
                    linefeeds++;
                }
                else
                    nline.Write(escapelf, 0, escapelf.Length);


                return nline.ToArray();
            }
        }
        /// <summary>
        /// Appends the current page stream to the accumulated print result.
        /// </summary>
        /// <param name="meta">MetaFile whose page is being closed.</param>
        public override void EndPage(MetaFile meta)
        {
            byte[] pageArray = currpageStream.ToArray();
            PrintResultStream.Write(pageArray, 0, pageArray.Length);
        }
        /// <summary>
        /// Returns the current page size in twips, falling back to the first standard page size when no
        /// size has been set.
        /// </summary>
        /// <param name="indexqt">Receives the page-size index; always zero for this driver.</param>
        /// <returns>The page size as a point of width and height in twips.</returns>
        public override Point GetPageSize(out int indexqt)
        {
            indexqt = 0;
            if (FPageWidth == 0)
            {
                FPageWidth = (int)Math.Round((double)MetaFile.PageSizeArray[0, 0] / 1000 * Twips.TWIPS_PER_INCH);
                FPageHeight = (int)Math.Round((double)MetaFile.PageSizeArray[0, 1] / 1000 * Twips.TWIPS_PER_INCH);

            }
            return new Point(FPageWidth, FPageHeight);
        }
        /// <summary>
        /// Computes the extent of a graphic; not supported by the text driver.
        /// </summary>
        /// <param name="astream">Stream holding the graphic data.</param>
        /// <param name="extent">Requested extent.</param>
        /// <param name="dpi">Resolution in dots per inch.</param>
        /// <returns>Never returns; always throws <see cref="NotImplementedException"/>.</returns>
        public override Point GraphicExtent(System.IO.MemoryStream astream, Point extent, int dpi)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Sets the page size from the given detail, applying the current orientation, and returns the
        /// resulting page dimensions in twips.
        /// </summary>
        /// <param name="psize">Page size detail, either a standard index or custom dimensions.</param>
        /// <returns>The resulting page size as a point of width and height in twips.</returns>
        public override Point SetPageSize(PageSizeDetail psize)
        {
            int newwidth, newheight;
            // Sets the page size for the pdf file, first if it's a qt page
            PageQt = psize.Index;
            if (psize.Custom)
            {
                PageQt = -1;
                newwidth = psize.CustomWidth;
                newheight = psize.CustomHeight;
            }
            else
            {
                newwidth = (int)Math.Round((double)MetaFile.PageSizeArray[psize.Index, 0] / 1000 * Twips.TWIPS_PER_INCH);
                newheight = (int)Math.Round((double)MetaFile.PageSizeArray[psize.Index, 1] / 1000 * Twips.TWIPS_PER_INCH);
            }
            if (FOrientation == OrientationType.Landscape)
            {
                FPageWidth = newheight;
                FPageHeight = newwidth;
            }
            else
            {
                FPageWidth = newwidth;
                FPageHeight = newheight;
            }
            return new Point(FPageWidth, FPageHeight);
        }
        /// <summary>
        /// Maps a font point size to a character-per-inch step when the selection is by size, otherwise
        /// returns the explicitly selected step.
        /// </summary>
        /// <param name="FontSize">Font point size to map.</param>
        /// <param name="select">Requested step, or <c>BySize</c> to derive it from the font size.</param>
        /// <returns>The character-per-inch step to use.</returns>
        public static PrintStepType FontSizeToStep(short FontSize, PrintStepType select)
        {
            PrintStepType aresult = PrintStepType.cpi10;
            if (select == PrintStepType.BySize)
            {
                switch (FontSize)
                {
                    case 8:
                        aresult = PrintStepType.cpi17;
                        break;
                    case 9:
                        aresult = PrintStepType.cpi15;
                        break;
                    case 10:
                        aresult = PrintStepType.cpi12;
                        break;
                    case 11:
                    case 12:
                        aresult = PrintStepType.cpi10;
                        break;
                    case 13:
                    case 14:
                    case 15:
                        aresult = PrintStepType.cpi6;
                        break;
                    default:
                        if (FontSize < 8)
                            aresult = PrintStepType.cpi20;
                        else
                        if (FontSize > 15)
                            aresult = PrintStepType.cpi5;
                        break;
                }
            }
            else
                aresult = select;
            return aresult;
        }
        /// <summary>
        /// Measures the extent of a text object at the text driver's font steps, honouring word wrap,
        /// single-line and cut-text settings.
        /// </summary>
        /// <param name="aobj">Text object to measure.</param>
        /// <param name="extent">Maximum available extent in twips.</param>
        /// <returns>The measured extent as a point of width and height in twips.</returns>
        public override Point TextExtent(TextObjectStruct aobj, Point extent)
        {
            bool singleline;
            Rectangle rect;
            PrintStepType fontstep;
            Point maxextent = new Point(extent.X, extent.Y); ;
            if (aobj.FontRotation != 0)
                return extent;
            if (aobj.CutText)
            {
                maxextent = extent;
            }

            // single line
            singleline = ((aobj.Alignment & MetaFile.AlignmentFlags_SingleLine) > 0);
            fontstep = FontSizeToStep(aobj.FontSize, aobj.PrintStep);
            rect = new Rectangle(0, 0, extent.X, 0);

            string ntext = aobj.Text.Replace("" + (char)13 + (char)10, "" + (char)10);


            CalculateTextExtent(ntext, ref rect, aobj.WordWrap, singleline, fontstep, aobj.CutText);

            extent = new Point(rect.Right, rect.Bottom);
            if (aobj.CutText)
            {
                if (maxextent.Y < extent.Y)
                    extent = new Point(extent.X, maxextent.Y);
            }
            return extent;
        }
        /// <summary>
        /// Printer initialization at the start of a document: the driver's InitPrinter escape
        /// (<c>ESC @</c> on the Epson family) and, when the metafile asks for it, the drawer pulse
        /// before printing. This is what the Delphi driver has always emitted (rptextdriver.pas,
        /// NewDocument); the port had it commented out and callers were prepending <c>ESC @</c> by
        /// hand — a receipt printer left mid-state by the previous job (double width, a code page,
        /// an alignment) would otherwise print the next ticket that way.
        /// </summary>
        public void WriteInit()
        {
            byte[] init = GetEscape(PrinterRawOperation.InitPrinter);
            if (init.Length > 0)
                currpageStream.Write(init, 0, init.Length);
            if (FDrawerBefore)
            {
                init = GetEscape(PrinterRawOperation.Pulse);
                if (init.Length > 0)
                    currpageStream.Write(init, 0, init.Length);
            }
        }
        /// <summary>
        /// Emits the line-spacing escape sequence that matches the current lines-per-inch setting,
        /// computing the fractional spacing value when the driver requires it.
        /// </summary>
        public void WriteInterLine()
        {
            byte[] s = emptyByteArray;
            switch (FLinesPerInch)
            {
                case 600:
                    s = GetEscape(PrinterRawOperation.LineSpace6);
                    break;
                case 800:
                    s = GetEscape(PrinterRawOperation.LineSpace8);
                    break;
                default:
                    s = GetEscape(PrinterRawOperation.LineSpace7_72);
                    if ((FLinesPerInch != 972) || (s.Length == 0))
                    {
                        s = GetEscape(PrinterRawOperation.LineSpacen_216);
                        if (s.Length > 0)
                        {
                            byte nlines = (byte)Math.Round(1.0m / ((decimal)FLinesPerInch / (decimal)100) * (decimal)216);
                            byte[] newByte = new byte[s.Length + 1];
                            Array.Copy(s, newByte, s.Length);
                            newByte[newByte.Length - 1] = (byte)nlines;
                            s = newByte;
                        }
                        else
                        {
                            s = GetEscape(PrinterRawOperation.LineSpacen_180);
                            if (s.Length > 0)
                            {
                                byte nlines2 = (byte)Math.Round(1.0m / ((decimal)FLinesPerInch / (decimal)100) * (decimal)180);
                                byte[] newByte = new byte[s.Length + 1];
                                Array.Copy(s, newByte, s.Length);
                                newByte[newByte.Length - 1] = nlines2;
                                s = newByte;
                            }
                            else
                            {
                                s = GetEscape(PrinterRawOperation.Linespacen_60);
                                if (s.Length > 0)
                                {
                                    byte nlines3 = (byte)Math.Round(1.0m / ((decimal)FLinesPerInch / (decimal)100) * (decimal)180);
                                    byte[] newByte = new byte[s.Length + 1];
                                    Array.Copy(s, newByte, s.Length);
                                    newByte[newByte.Length - 1] = nlines3;
                                    s = newByte;
                                }
                            }

                        }

                    }
                    break;
            }
            if (s.Length > 0)
            {
                currpageStream.Write(s, 0, s.Length);
            }

        }
        /// <summary>
        /// Renders the requested page range of the metafile, laying out each page, writing its lines and
        /// finalizing the document.
        /// </summary>
        /// <param name="meta">MetaFile to print.</param>
        /// <returns>True when at least the first requested page was rendered; false otherwise.</returns>
        public override bool Print(MetaFile meta)
        {
            currpageStream = new System.IO.MemoryStream();
            bool aresult = base.Print(meta);
            int FCurrentPage = FromPage - 1;
            meta.RequestPage(FCurrentPage);
            if (meta.Pages.CurrentCount < FromPage)
                return false;
            SetPageSize(meta.Pages[0].PageDetail);
            SetOrientation(meta.Orientation);




            MetaPage apage;
            while (meta.Pages.CurrentCount > FCurrentPage)
            {
                apage = meta.Pages[FCurrentPage];
                if (FCurrentPage == (FromPage - 1))
                {
                    WriteInit();
                    WriteInterLine();
                    WritePageSize();
                }
                else
                {
                    if ((FCurrentPage <= (ToPage - 1)) && (FCurrentPage >= (FromPage - 1)))
                        NewPage(meta, apage);
                }
                DrawPage(meta, apage);
                FCurrentPage++;
                meta.RequestPage(FCurrentPage);
                if ((FCurrentPage > (ToPage - 1)) || (FCurrentPage >= meta.Pages.CurrentCount))
                {
                    bool cutclearlines = false;
                    if (IsReceiptDriver(FPrinterDriver))
                        cutclearlines = true;

                    WriteCurrentPage(cutclearlines);
                    EndPage(meta);
                    break;
                }
                else
                {
                    WriteCurrentPage(false);
                    EndPage(meta);
                }
            }
            EndDocument(meta);
            return true;
        }
        /// <summary>
        /// Starts a new page, resetting the page stream and recalculating the number of print lines.
        /// </summary>
        /// <param name="meta">MetaFile that owns the page.</param>
        /// <param name="page">MetaPage being started.</param>
        public override void NewPage(MetaFile meta, MetaPage page)
        {
            currpageStream = new System.IO.MemoryStream();
            RecalcSize();
            base.NewPage(meta, page);
        }
        /// <summary>
        /// Recreates the list of print lines so that it matches the page height at the current
        /// lines-per-inch setting.
        /// </summary>
        public void RecalcSize()
        {
            int numberoflines = (int)Math.Round(Twips.TwipsToInch(FPageHeight) * ((decimal)FLinesPerInch / 100));
            Lines.Clear();
            for (int i = 0; i < numberoflines; i++)
            {
                Lines.Add(new PrintLine());
            }
            linefeeds = 0;
        }
        /// <summary>
        /// Emits the escape sequence that sets the page length for the active driver and selects the
        /// regular font, throwing if the page exceeds 255 lines.
        /// </summary>
        public void WritePageSize()
        {
            // Write interline

            byte[] s = emptyByteArray;
            // Set line space
            if (Lines.Count > 255)
            {
                throw new Exception("Se ha excedido el máximo número de líneas 255 en writepagesize");
            }
            byte plinecount = (byte)Lines.Count;
            switch (FPrinterDriver)
            {
                case "EPSON":
                case "EPSON-ESCPQ":
                case "EPSON-ESCP":
                case "EPSON-MASTER":
                case "IBMPROPRINTER":
                    s = new byte[] { 27, (byte)'C', plinecount };
                    break;
                case "HP-PCL":
                    s = new byte[] { 27, 38, 108, plinecount, 80 };
                    break;
            }
            if (s.Length > 0)
                currpageStream.Write(s, 0, s.Length);
            // Select regular font
            s = GetEscape(PrinterRawOperation.Normal);
            if (s.Length > 0)
                currpageStream.Write(s, 0, s.Length);
        }
        /// <summary>
        /// Returns the nearest character-per-inch step supported by the active printer driver for the
        /// requested step.
        /// </summary>
        /// <param name="fontstep">Requested character-per-inch step.</param>
        /// <returns>The closest allowed step for the current driver.</returns>
        public PrintStepType NearestFontStep(PrintStepType fontstep)
        {
            return allowedsteps[fontstep];
        }
        /*function TRpTextDriver.NearestFontStep(FontStep:TRpFontStep):TRpFontStep;
        var
         maxallowed:TRpFontStep;
         i:TRpFontStep;
        begin
         maxallowed:=rpcpi10;
         for i:=Low(TRpFontStep) to High(TRpFontStep) do
         begin
          if allowedsizes[i] then
          begin
           maxallowed:=i;
           break;
          end;
         end;
         Result:=maxallowed;
         i:=FontStep;
         while i>maxallowed do
         begin
          if allowedsizes[i] then
          begin
           Result:=i;
           break;
          end;
          dec(i);
         end;
        end;


                 */
        /// <summary>
        /// Returns the width of a single character, in twips, for the given character-per-inch step.
        /// </summary>
        /// <param name="step">Character-per-inch step.</param>
        /// <returns>The character width in twips.</returns>
        public double StepToTwips(PrintStepType step)
        {
            double aresult = (double)Twips.TWIPS_PER_INCH / 10.0;

            switch (step)
            {
                case PrintStepType.cpi20:
                    aresult = (double)Twips.TWIPS_PER_INCH / 20.0;
                    break;
                case PrintStepType.cpi17:
                    aresult = (double)Twips.TWIPS_PER_INCH / 17.14;
                    break;
                case PrintStepType.cpi15:
                    aresult = (double)Twips.TWIPS_PER_INCH / 15.0;
                    break;
                case PrintStepType.cpi12:
                    aresult = (double)Twips.TWIPS_PER_INCH / 12.0;
                    break;
                case PrintStepType.cpi10:
                    aresult = (double)Twips.TWIPS_PER_INCH / 10.0;
                    break;
                case PrintStepType.cpi6:
                    aresult = (double)Twips.TWIPS_PER_INCH / 6.0;
                    break;
                case PrintStepType.cpi5:
                    aresult = (double)Twips.TWIPS_PER_INCH / 5.0;
                    break;
            }

            return aresult;
        }
        /// <summary>
        /// Returns the width in twips contributed by a character, treating null, carriage-return and
        /// line-feed characters as zero width.
        /// </summary>
        /// <param name="charcode">Character to measure.</param>
        /// <param name="step">Character-per-inch step.</param>
        /// <returns>The character width in twips.</returns>
        public double CalcCharWidth(char charcode, PrintStepType step)
        {
            if ((charcode == (char)0) || (charcode == (char)13) || (charcode == (char)10))
                return 0;
            else
                return StepToTwips(step);
        }
        bool IsSign(char c)
        {
            return ((c == ',') || (c == '.') || (c == '-') || (c == ' ') || (c == ':') || (c == ';'));
        }
        /// <summary>
        /// Whether one more laid-out line of a clipped text object is kept. Lines past the object's
        /// height are dropped — except the FIRST one, which always prints. A text driver has no
        /// partial rows: dropping the only line of an object that is a hair shorter than a print
        /// line (a 230-twip row at 6 lpi, which is 240) does not clip it, it deletes it, and a
        /// receipt loses its amounts without a word. Every object designed taller than a line is
        /// unaffected; the ones that used to vanish now show their first line, which is what any
        /// designer meant.
        /// </summary>
        /// <param name="already">Lines already kept for this object.</param>
        /// <param name="lineheight">Height of one print line in twips.</param>
        /// <param name="rectheight">Height of the object in twips.</param>
        private static bool LineFits(int already, int lineheight, int rectheight)
        {
            if (already == 0)
                return true;
            return (lineheight * already + lineheight) <= rectheight;
        }
        /// <summary>
        /// Computes the extent of a text block and fills the internal line-info list used for layout,
        /// applying word wrapping, single-line collapsing and clipping to the rectangle height.
        /// </summary>
        /// <param name="text">Text to measure.</param>
        /// <param name="rect">On input the bounding rectangle; on output the measured extent in twips.</param>
        /// <param name="wordbreak">When true, text is wrapped on word boundaries.</param>
        /// <param name="singleline">When true, line breaks are replaced with spaces.</param>
        /// <param name="fontstep">Character-per-inch step used to measure the text.</param>
        /// <param name="doclip">When true, lines that overflow the rectangle height are dropped.</param>
        public void CalculateTextExtent(string text, ref Rectangle rect, bool wordbreak, bool singleline, PrintStepType fontstep, bool doclip)
        {
            bool dolineinfo = true;
            // Calculate leading and line spacing
            int linespacing = (int)Math.Round((double)Twips.TWIPS_PER_INCH / ((double)FLinesPerInch / 100.0));
            int leading = 0;
            int maxrightindex = -1;

            fontstep = NearestFontStep(fontstep);

            StringBuilder currentline = new StringBuilder();
            double currentwidth = 0;
            double maxwidth = 0;
            double newsize = 0;
            int linebreakpos = 0;
            bool wasspace = false;
            int infocount = 0;
            double lastsize = 0;
            double lastsizewithoutspace = 0;
            int lastindexwithoutspace = 0;
            int currenttoppos = 0;
            double recwidth = (double)(rect.Width);
            if (dolineinfo)
                linfos.Clear();
            // Replace cr/lf for only cfs
            //string astring = text.Replace("" + (char)13 + (char)10, "" + (char)10);
            string astring = text;
            if (singleline)
            {
                astring = astring.Replace("" + (char)13, " ");
                astring = astring.Replace("" + (char)10, " ");
            }

            int i = 0;
            int startposition = 0;
            LineInfo linfo = new LineInfo();
            while (i < astring.Length)
            {
                // Check for LF
                if ((astring[i] == (char)10))
                {
                    // Add the line
                    int cutindex = i;
                    if (wasspace)
                    {
                        cutindex = lastindexwithoutspace + 1;
                        currentwidth = lastsizewithoutspace;
                    }
                    if (maxrightindex >= 0)
                        cutindex = maxrightindex;
                    linfo.LastLine = true;
                    linfo.Position = startposition;
                    linfo.Step = fontstep;
                    linfo.Size = cutindex - startposition;
                    if (linfo.Size < 0)
                        linfo.Size = 0;
                    linfo.Width = (int)Math.Round(currentwidth);
                    if (currentwidth > maxwidth)
                        maxwidth = currentwidth;
                    linfo.TopPos = currenttoppos - leading;
                    linfo.Height = linespacing;
                    currenttoppos = currenttoppos + linespacing;
                    bool doinsert = true;
                    if (doclip)
                        doinsert = LineFits(linfos.Count, linfo.Height, rect.Height);
                    if (doinsert)
                    {
                        linfos.Add(linfo);
                        infocount++;
                    }
                    currentwidth = 0;
                    startposition = i + 1;
                    maxrightindex = -1;
                }
                else
                {
                    newsize = CalcCharWidth(astring[i], fontstep);
                    // If the character fits inside the line
                    if ((currentwidth + newsize <= recwidth) || (!wordbreak))
                    {
                        if (currentwidth + newsize > recwidth)
                            if (maxrightindex < 0)
                                maxrightindex = i - 1;
                        if (astring[i] == ' ')
                        {
                            if (!wasspace)
                            {
                                wasspace = true;
                                lastsizewithoutspace = currentwidth;
                                if (i > startposition)
                                {
                                    lastindexwithoutspace = i - 1;
                                    linebreakpos = i - 1;
                                }
                                else
                                    lastindexwithoutspace = 0;
                            }
                        }
                        else
                        {
                            wasspace = false;
                            if (IsSign(astring[i]))
                            {
                                linebreakpos = i;
                                lastsize = currentwidth + newsize;
                            }
                        }
                        currentwidth = currentwidth + newsize;
                    }
                    // When the character does not fit
                    else
                    {
                        if (wordbreak)
                        {
                            // Add the line
                            if ((currentwidth == 0) && (startposition == i))
                                i++;
                            int cutindex = i;
                            if (linebreakpos > 0)
                            {
                                cutindex = linebreakpos + 1;
                                i = linebreakpos;
                                currentwidth = lastsize;
                            }
                            else
                                i--;
                            if (wasspace)
                            {
                                cutindex = lastindexwithoutspace + 1;
                                currentwidth = lastsizewithoutspace;
                            }
                            if (maxrightindex >= 0)
                                cutindex = maxrightindex;
                            linfo.LastLine = false;
                            linfo.Position = startposition;
                            linfo.Step = fontstep;
                            linfo.Size = cutindex - startposition;
                            if (linfo.Size < 0)
                                linfo.Size = 0;
                            linfo.Width = (int)Math.Round((currentwidth));
                            if (currentwidth > maxwidth)
                                maxwidth = currentwidth;
                            linfo.TopPos = currenttoppos - leading;
                            linfo.Height = linespacing;
                            currenttoppos = currenttoppos + linespacing;
                            bool doinsert = true;
                            if (doclip)
                                doinsert = LineFits(linfos.Count, linfo.Height, rect.Height);
                            if (doinsert)
                            {
                                linfos.Add(linfo);
                                infocount++;
                            }
                            currentwidth = 0;
                            linebreakpos = 0;
                            startposition = i + 1;
                            maxrightindex = -1;
                            // Skip spaces
                            while (startposition < astring.Length - 1)
                            {
                                if (astring[startposition] == ' ')
                                {
                                    startposition++;
                                    i = startposition - 1;
                                }
                                else
                                    break;
                            }
                        }
                        else
                        {
                            if (maxrightindex < 0)
                                maxrightindex = i - 1;
                        }
                    }
                }
                i++;
            }
            // Check for LF
            if (startposition < astring.Length)
            {
                // Add the line
                int cutindex = i;
                if (wasspace)
                {
                    cutindex = lastindexwithoutspace + 1;
                    currentwidth = lastsizewithoutspace;
                }
                if (maxrightindex >= 0)
                    cutindex = maxrightindex;
                linfo.LastLine = true;
                linfo.Position = startposition;
                linfo.Step = fontstep;
                linfo.Size = cutindex - startposition;
                if (linfo.Size < 0)
                    linfo.Size = 0;
                linfo.Width = (int)Math.Round(currentwidth);
                if (currentwidth > maxwidth)
                    maxwidth = currentwidth;
                linfo.TopPos = currenttoppos - leading;
                linfo.Height = linespacing;
                currenttoppos = currenttoppos + linespacing;
                bool doinsert = true;
                if (doclip)
                    doinsert = LineFits(linfos.Count, linfo.Height, rect.Height);
                if (doinsert)
                {
                    linfos.Add(linfo);
                    infocount++;
                }
                maxrightindex = -1;
            }
            int totalheight = 0;
            if (infocount > 0)
                totalheight = infocount * linespacing + leading;
            Rectangle arec = new Rectangle(rect.Left, rect.Top,
                                           (int)Math.Round((maxwidth)),
                                           totalheight);
            rect = arec;

        }
        /// <summary>
        /// Alternate text-extent calculation that fills the internal line-info list, applying word
        /// wrapping and single-line handling; kept for compatibility with the original layout routine.
        /// </summary>
        /// <param name="text">Text to measure.</param>
        /// <param name="rect">On input the bounding rectangle; on output the measured extent in twips.</param>
        /// <param name="WordBreak">When true, text is wrapped on word boundaries.</param>
        /// <param name="singleline">When true, line breaks do not start new lines.</param>
        /// <param name="fontstep">Character-per-inch step used to measure the text.</param>
        public void CalculateTextExtent2(string text, ref Rectangle rect, bool WordBreak, bool singleline, PrintStepType fontstep)
        {
            string astring;
            int i;
            double asize;
            Rectangle arec;
            int position;
            LineInfo info;
            double maxwidth;
            double newsize;
            double recwidth;
            int linebreakpos;
            bool nextline;
            double alastsize;
            bool lockspace;
            bool createsnewline;
            bool DescPos = false;

            astring = text;
            fontstep = NearestFontStep(fontstep);
            arec = new Rectangle(0, 0, rect.Width, 0);

            asize = 0.0;
            createsnewline = false;
            position = 0;
            linebreakpos = -1;
            maxwidth = 0;
            recwidth = rect.Right - rect.Left;
            nextline = false;
            i = 0;
            alastsize = 0;
            lockspace = false;
            linfos.Clear();

            while (i < astring.Length)
            {
                newsize = CalcCharWidth(astring[i], fontstep);
                if (!((astring[i] == ' ') || (astring[i] == (char)10) || (astring[i] == (char)13)))
                {
                    lockspace = false;
                    if (WordBreak)
                    {
                        if ((asize + newsize) > recwidth)
                        {
                            if (linebreakpos >= 0)
                            {
                                i = linebreakpos;
                            }
                            else
                                i--;
                            nextline = true;
                            asize = alastsize;
                            linebreakpos = -1;
                        }
                        else
                        {
                            if ((astring[i] == '.') || (astring[i] == ',') || (astring[i] == '-') || (astring[i] == ' '))
                            {
                                linebreakpos = i;
                                if (astring[i] == ' ')
                                {
                                    if (!lockspace)
                                    {
                                        alastsize = asize;
                                        lockspace = true;
                                    }
                                }
                                else
                                {
                                    alastsize = asize + newsize;
                                }
                            }
                            asize = asize + newsize;
                        }
                    }
                    else
                        asize = asize + newsize;
                }
                else
                {
                    asize = asize + newsize;
                }
                if (!singleline)
                {
                    if (astring[i] == (char)10)
                    {
                        nextline = true;
                        DescPos = true;
                        createsnewline = true;
                    }
                }
                if (asize > maxwidth)
                    maxwidth = asize;
                if (nextline)
                {
                    info = new LineInfo();
                    nextline = false;
                    info.Position = position;
                    info.LastLine = createsnewline;
                    info.Size = i - position;
                    if (DescPos)
                    {
                        DescPos = false;
                        info.Size = info.Size - 2;
                    }
                    info.Width = (int)Math.Round(asize);
                    info.Height = (int)Math.Round((double)Twips.TWIPS_PER_INCH / ((double)FLinesPerInch / 100.0));
                    info.TopPos = arec.Bottom;
                    arec = new Rectangle(arec.Left, arec.Top, arec.Width, arec.Height + info.Height);
                    asize = 0;
                    position = i + 1;
                    linfos.Add(info);
                    createsnewline = false;
                    if (i < astring.Length - 1)
                    {
                        if (astring[i + 1] == ' ')
                        {
                            i++;
                            position = i + 1;
                        }
                    }
                }
                i++;
            }
            arec = new Rectangle(arec.Left, arec.Top, (int)Math.Round(maxwidth + 1), arec.Height);
            if (position < astring.Length)
            {
                info = new LineInfo();
                info.Position = position;
                info.Size = astring.Length - position;
                info.Width = (int)Math.Round(asize + 1);
                info.Height = (int)Math.Round(System.Convert.ToDouble(Twips.TWIPS_PER_INCH) / (System.Convert.ToDouble(FLinesPerInch) / 100));
                info.TopPos = arec.Bottom;
                arec = new Rectangle(arec.Left, arec.Top, arec.Width, arec.Height + info.Height);
                linfos.Add(info);
            }
            rect = new Rectangle(arec.Left, arec.Top, arec.Width, linfos.Count * (int)Math.Round(System.Convert.ToDouble(Twips.TWIPS_PER_INCH) / (System.Convert.ToDouble(FLinesPerInch) / 100)));
        }
    }
    /// <summary>
    /// One physical text row of a page being built by the text driver, holding the line contents,
    /// its character-per-inch font step, and the positioned text fragments keyed by column.
    /// </summary>
    public class PrintLine
    {
        /// <summary>
        /// Positioned text fragments on this line, keyed by their starting column.
        /// </summary>
        public SortedList<int, PosText> texts;
        /// <summary>
        /// Character-per-inch step applied to the whole line.
        /// </summary>
        public PrintStepType FontStep;
        /// <summary>
        /// The full character content of the line, padded with spaces to the page width.
        /// </summary>
        public string Value;
        /// <summary>
        /// Raw device commands emitted BEFORE the text of this line — a barcode the printer draws
        /// natively (see <see cref="PrintOutText.DrawObject"/> for <see cref="MetaObjectBarcode"/>).
        /// Null until something is attached; the common case pays nothing.
        /// </summary>
        public List<byte[]> RawBefore;
        /// <summary>
        /// True when a native symbol printed on this line (or above it) already consumed the vertical
        /// space the line represents: the printer fed the paper while drawing the barcode, so an
        /// empty consumed line is not emitted, or the symbol would be followed by the blank rows the
        /// layout had reserved for it. A consumed line that does carry text still prints its text.
        /// </summary>
        public bool Consumed;
        /// <summary>
        /// Initializes an empty print line with the default 10 cpi step.
        /// </summary>
        public PrintLine()
        {
            Value = "";
            texts = new SortedList<int, PosText>();
            FontStep = PrintStepType.cpi10;
        }
    }
    /// <summary>
    /// A text fragment placed at a column on a print line, recording its position, length, and the
    /// style attributes (bold, italic, underline, strikeout, red, regular) to apply when emitting it.
    /// </summary>
    public struct PosText
    {
        /// <summary>
        /// Starting column of the fragment on its print line.
        /// </summary>
        public int Position;
        /// <summary>
        /// Length of the fragment in characters.
        /// </summary>
        public int nSize;
        /// <summary>
        /// True when the fragment is underlined.
        /// </summary>
        public bool underline;
        /// <summary>
        /// True when the fragment is italic.
        /// </summary>
        public bool italic;
        /// <summary>
        /// True when the fragment is printed in red.
        /// </summary>
        public bool red;
        /// <summary>
        /// True when the fragment is bold.
        /// </summary>
        public bool bold;
        /// <summary>
        /// True when the fragment is struck out.
        /// </summary>
        public bool strokeout;
        /// <summary>
        /// True when the fragment has no style attributes and can be emitted without style escapes.
        /// </summary>
        public bool regular;
    }
}
