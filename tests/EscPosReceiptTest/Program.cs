using System;
using System.IO;
using System.Linq;
using System.Text;
using Reportman.Drawing;
using Reportman.Reporting;

namespace EscPosReceiptTest
{
    // Validates the receipt (ESC/POS) path of the text driver:
    // 1. A QR barcode item reaches an ESC/POS driver as a MetaObjectBarcode (not as module rectangles)
    //    and comes out as the native GS ( k sequence carrying the item's data, alignment and ECC.
    // 2. Any other driver keeps receiving the drawn modules — the metafile of a preview never
    //    carries the new object type.
    // 3. MetaObjectBarcode survives a metafile save/load round trip.
    // 4. The rows the barcode box reserves are consumed: no blank lines follow the symbol.
    // 5. Init at the start; cut, reset and drawer pulse at the end, in that order (Delphi parity).
    // 6. A clipped text object one hair shorter than a print line still prints its first line.
    // 7. Font steps are resolved once: a 16 pt line is emitted double width and its columns
    //    are measured for that width.
    class Program
    {
        static int failures = 0;

        static void Check(bool condition, string name, string detail = "")
        {
            if (condition)
                Console.WriteLine("[PASS] " + name);
            else
            {
                Console.WriteLine("[FAIL] " + name + (detail.Length > 0 ? " -> " + detail : ""));
                failures++;
            }
        }

        const string Url = "https://prewww2.aeat.es/wlpl/TIKE-CONT/ValidarQR?nif=B12345678&numserie=TI%2F000123&fecha=16-08-2026&importe=34.90";
        const int Line = 240;   // one row at 6 lpi

        static Report BuildReceipt()
        {
            Report report = new Report();
            report.CreateNew();
            report.PageSize = PageSizeType.User;
            report.CustomPageWidth = 4536;      // 80 mm
            report.CustomPageHeight = 22680;    // 40 cm of roll
            report.LeftMargin = 0;
            report.RightMargin = 0;
            report.TopMargin = 0;
            report.BottomMargin = 0;

            SubReport subrep = report.SubReports[0];
            Section detail = subrep.Sections[subrep.FirstDetail];
            detail.Width = 4536;
            detail.Height = 26 * Line;

            AddLabel(report, detail, 0, 0, 4536, Line, "QR tributario:", 9, TextAlignType.Center);
            // The QR box: 40 mm square, centred, from row 1 to row 10 (2268 twips = 9.45 rows)
            BarcodeItem qr = new BarcodeItem
            {
                Report = report, Section = detail, BarType = BarcodeType.CodeQR,
                Expression = "'" + Url + "'", ECCLevel = 3,
                PosX = (4536 - 2268) / 2, PosY = Line, Width = 2268, Height = 2268,
                BackColor = 0xFFFFFF, BColor = 0
            };
            report.GenerateNewName(qr);
            detail.Components.Add(qr);
            AddLabel(report, detail, 0, 11 * Line, 4536, Line, "VERI*FACTU", 9, TextAlignType.Center);
            // A row a hair shorter than a print line, clipped: must still print.
            AddLabel(report, detail, 0, 12 * Line, 4536, 230, "Camiseta rayas 25,00", 8, TextAlignType.Left);
            // 16 pt total: double width on the TM-88.
            AddLabel(report, detail, 0, 13 * Line, 1800, 2 * Line, "TOTAL", 16, TextAlignType.Left);
            AddLabel(report, detail, 1800, 13 * Line, 4536 - 1800, 2 * Line, "34,90", 16, TextAlignType.Right);
            // An EAN-13 and a PDF417 further down: the linear and the stacked symbologies.
            AddLabel(report, detail, 0, 15 * Line, 4536, Line, "Peluche pequeño ñ", 8, TextAlignType.Left);
            BarcodeItem ean = new BarcodeItem
            {
                Report = report, Section = detail, BarType = BarcodeType.CodeEAN13,
                Expression = "'8412345678905'", PosX = 500, PosY = 16 * Line, Width = 3000, Height = 3 * Line,
                BackColor = 0xFFFFFF, BColor = 0
            };
            report.GenerateNewName(ean);
            detail.Components.Add(ean);
            BarcodeItem pdf = new BarcodeItem
            {
                Report = report, Section = detail, BarType = BarcodeType.CodePDF417,
                Expression = "'FACTURA SIMPLIFICADA 34,90 EUR'", ECCLevel = 2, NumColumns = 4, PosX = 0, PosY = 20 * Line, Width = 4536, Height = 5 * Line,
                BackColor = 0xFFFFFF, BColor = 0
            };
            report.GenerateNewName(pdf);
            detail.Components.Add(pdf);
            report.ActionAfter = true;      // open the drawer after the receipt
            return report;
        }

        static void AddLabel(Report report, Section detail, int x, int y, int w, int h, string text, short size, TextAlignType align)
        {
            LabelItem label = new LabelItem();
            label.Report = report;
            report.GenerateNewName(label);
            label.Section = detail;
            label.PosX = x; label.PosY = y; label.Width = w; label.Height = h;
            label.WFontName = "Arial"; label.LFontName = "Arial";
            label.FontSize = size;
            label.Alignment = align;
            label.VAlignment = TextAlignVerticalType.Center;
            label.CutText = true;
            label.WordWrap = false;
            label.Transparent = true;
            label.Text = text;
            detail.Components.Add(label);
        }

        static byte[] PrintText(Report report, string driverName)
        {
            report.MetaFile.Clear();
            using (PrintOutText driver = new PrintOutText())
            {
                driver.ForceDriverName = driverName;
                driver.OverrideOemConvert = PrintOutText.OemConvertOverride.True;
                driver.Print(report.MetaFile);
                return driver.PrintResultStream.ToArray();
            }
        }

        static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
        {
            for (int i = from; i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        static void Main(string[] args)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Report report = BuildReceipt();

                // --- 1. ESC/POS driver: native QR ---
                byte[] escpos = PrintText(report, "EPSONTM88IICUT");
                MetaPage page = report.MetaFile.Pages[0];
                int barcodes = page.Objects.Count(o => o.MetaType == MetaObjectType.Barcode);
                int draws = page.Objects.Count(o => o.MetaType == MetaObjectType.Draw);
                Check(barcodes == 3 && draws == 0, "ESC/POS metafile carries the barcodes as Barcode objects and no modules",
                    $"barcodes={barcodes} draws={draws}");
                MetaObjectBarcode bar = page.Objects.OfType<MetaObjectBarcode>().First(o => o.Symbology == MetaBarcodeSymbology.QR);
                Check(page.GetText(bar) == Url && bar.Symbology == MetaBarcodeSymbology.QR && bar.Ecc == MetaBarcodeEcc.M
                      && bar.Modules > 21 && bar.Rows == bar.Modules,
                    "Barcode object: data, symbology QR, ECC M and a square module count",
                    $"modules={bar.Modules} rows={bar.Rows} ecc={bar.Ecc}");

                byte[] model = { 29, (byte)'(', (byte)'k', 4, 0, 49, 65, 50, 0 };
                byte[] eccM = { 29, (byte)'(', (byte)'k', 3, 0, 49, 69, 49 };
                byte[] print = { 29, (byte)'(', (byte)'k', 3, 0, 49, 81, 48 };
                byte[] payload = Encoding.Latin1.GetBytes(Url);
                int k = payload.Length + 3;
                byte[] store = { 29, (byte)'(', (byte)'k', (byte)(k & 0xFF), (byte)(k >> 8), 49, 80, 48 };
                int iModel = IndexOf(escpos, model);
                int iStore = IndexOf(escpos, store);
                int iData = iStore >= 0 ? IndexOf(escpos, payload, iStore) : -1;
                int iPrint = IndexOf(escpos, print);
                Check(iModel >= 0 && IndexOf(escpos, eccM) > iModel && iStore > iModel && iData == iStore + store.Length && iPrint > iData,
                    "GS ( k: model 2, ECC M, store with the URL, print — in order");
                int iCenter = IndexOf(escpos, new byte[] { 27, (byte)'a', 1 });
                int iLeftAgain = IndexOf(escpos, new byte[] { 27, (byte)'a', 0 }, iPrint);
                Check(iCenter >= 0 && iCenter < iModel && iLeftAgain > iPrint, "Centred before the symbol, back to left after it");
                // module size: 320 dots over the modules ZXing returned
                int iModule = IndexOf(escpos, new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 49, 67 });
                int expectedModule = Math.Max(3, Math.Min(16, 320 / bar.Modules));
                Check(iModule >= 0 && escpos[iModule + 7] == expectedModule, "Module size fills the 40 mm box",
                    $"module={escpos[iModule + 7]} expected={expectedModule}");

                // --- 4. Consumed rows: no blank line between the symbol and the next text ---
                string cp850 = Encoding.GetEncoding(850).GetString(escpos);
                int iVeri = cp850.IndexOf("VERI*FACTU", StringComparison.Ordinal);
                string between = cp850.Substring(iPrint + print.Length, iVeri - iPrint - print.Length);
                int blankLines = between.Count(c => c == '\n');
                Check(blankLines <= 2, "At most the phrase's own row separates the symbol from the phrase (no reserved rows)",
                    $"newlines between symbol and phrase = {blankLines}");

                // --- 5. Init, cut, reset, drawer ---
                Check(escpos.Length > 2 && escpos[0] == 27 && escpos[1] == 64, "Document starts with ESC @");
                int iCut = IndexOf(escpos, new byte[] { 27, (byte)'d', 4, 29, (byte)'V', 66, 0, 27, 64 });   // feed to the cutter, cut, reset (2026-09-09)
                int iPulse = IndexOf(escpos, new byte[] { 27, (byte)'p', 0, 100, 100 });
                Check(iCut > iPrint && iPulse > iCut && iPulse + 5 == escpos.Length, "Ends with cut, reset, then the drawer pulse");

                // --- 6. Short row keeps its first line ---
                Check(cp850.Contains("Camiseta rayas 25,00"), "A 230-twip clipped row still prints its first line");

                // --- 7. Font step resolved once ---
                int iWide = IndexOf(escpos, new byte[] { 27, (byte)'!', 32 });
                Check(iWide >= 0, "16 pt row is emitted double width (ESC ! 32)");
                Check(cp850.Contains("34,90"), "The double-width amount is not truncated");

                // --- 2. Another driver: modules, no Barcode object ---
                PrintText(report, "EPSON");
                MetaPage page2 = report.MetaFile.Pages[0];
                Check(page2.Objects.Count(o => o.MetaType == MetaObjectType.Barcode) == 0
                      && page2.Objects.Count(o => o.MetaType == MetaObjectType.Draw) > 100,
                    "A driver without native barcodes gets the drawn modules and no Barcode object");

                // --- 8. Linear and stacked symbologies go native too ---
                int iEan = IndexOf(escpos, new byte[] { 29, (byte)'k', 67, 13 });
                int iEanData = iEan >= 0 ? IndexOf(escpos, Encoding.ASCII.GetBytes("8412345678905"), iEan) : -1;
                Check(iEan >= 0 && iEanData == iEan + 4, "EAN-13 comes out as GS k 67 with its 13 digits");
                int iH = IndexOf(escpos, new byte[] { 29, (byte)'h' }), iW = IndexOf(escpos, new byte[] { 29, (byte)'w' });
                Check(iH >= 0 && iW >= 0 && iH < iEan && iW < iEan, "Bar height and module width precede the linear code");
                int iPdf = IndexOf(escpos, new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 65, 4 });
                int iPdfPrint = IndexOf(escpos, new byte[] { 29, (byte)'(', (byte)'k', 3, 0, 48, 81, 48 });
                int iPdfData = IndexOf(escpos, Encoding.ASCII.GetBytes("FACTURA SIMPLIFICADA"));
                Check(iPdf >= 0 && iPdfData > iPdf && iPdfPrint > iPdfData, "PDF417 comes out as GS ( k cn 48 with 4 columns, stored and printed");

                // --- 9. The drivers that declare their character set ---
                byte[] bytes850 = PrintText(report, "EPSONTM88CP850CUT");
                Check(bytes850.Length > 5 && bytes850[0] == 27 && bytes850[1] == 64 && bytes850[2] == 27 && bytes850[3] == (byte)'t' && bytes850[4] == 2,
                    "EPSONTM88CP850CUT starts with ESC @ ESC t 2");
                Check(IndexOf(bytes850, new byte[] { (byte)'q', (byte)'u', (byte)'e', 0xA4, (byte)'o' }) >= 0, "...and the enye travels as cp850 0xA4");
                byte[] utf8 = PrintText(report, "EPSONTM88UTF8CUT");
                Check(utf8.Length > 9 && utf8[2] == 28 && utf8[3] == (byte)'(' && utf8[4] == (byte)'C' && utf8[7] == 48 && utf8[8] == 2,
                    "EPSONTM88UTF8CUT starts with ESC @ FS ( C 2 0 48 2");
                Check(IndexOf(utf8, new byte[] { (byte)'q', (byte)'u', (byte)'e', 0xC3, 0xB1, (byte)'o' }) >= 0, "...and the enye travels as UTF-8 C3 B1");
                Check(IndexOf(utf8, new byte[] { 29, (byte)'V', 66, 0, 27, 64 }) > 0 && IndexOf(utf8, model) > 0, "The new drivers keep the cut and the native QR");
                byte[] legacy = PrintText(report, "EPSONTM88IICUT");
                Check(legacy[0] == 27 && legacy[1] == 64 && !(legacy[2] == 27 && legacy[3] == (byte)'t'), "The legacy driver still says nothing about the character set");

                // --- 3. Round trip ---
                PrintText(report, "EPSONTM88IICUT");
                using (MemoryStream ms = new MemoryStream())
                {
                    report.MetaFile.SaveToStream(ms, false);
                    ms.Position = 0;
                    MetaFile loaded = new MetaFile();
                    loaded.LoadFromStream(ms, true);
                    MetaObjectBarcode back = loaded.Pages[0].Objects.OfType<MetaObjectBarcode>().FirstOrDefault();
                    Check(back != null && loaded.Pages[0].GetText(back) == Url && back.Modules == bar.Modules
                          && back.Ecc == MetaBarcodeEcc.M && back.Width == 2268,
                        "MetaObjectBarcode survives the metafile round trip");
                }

                Console.WriteLine();
                Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
                Environment.Exit(failures == 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] " + ex);
                Environment.Exit(2);
            }
        }
    }
}
