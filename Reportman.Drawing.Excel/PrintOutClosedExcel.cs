using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Reportman.Drawing
{
    /// <summary>
    /// Print-out driver that exports a rendered report metafile to an XLSX
    /// workbook using ClosedXML, mapping text and image objects onto worksheet
    /// cells by their page position with optional single-sheet output.
    /// </summary>
    /// <remarks>
    /// Intelisis branch (2026-09-25):
    /// <list type="bullet">
    /// <item>Output to memory (<see cref="ResultStream"/>) when <see cref="FileName"/> is empty, and a configurable <see cref="SheetName"/>.</item>
    /// <item>Strict number detection: only text that is exactly a number in <see cref="Culture"/> (optional sign, currency symbol,
    /// percent, parentheses) becomes a number, with a number format that mirrors the printed text (decimals, grouping, currency).
    /// Codes stay text: leading zeros ("00123") and long digit runs without grouping (barcodes, ids).</item>
    /// <item>Dates are detected with exact patterns (<see cref="DatePatterns"/>), never with a free parse, and keep their display format.</item>
    /// <item>Formula injection: text never becomes a formula. Text starting with = + - @ tab or CR is prefixed with an apostrophe
    /// (<see cref="NeutralizeFormulas"/>).</item>
    /// <item>Column widths follow the report layout; images that ClosedXML cannot read are skipped instead of aborting the export.</item>
    /// </list>
    /// </remarks>
    public class PrintOutClosedExcel : PrintOut, IDisposable
    {
        DateTime mmfirst;
        int npage;
        int nrecord;
        MetaFile nmeta;
        int PageQt;
        int FPageWidth, FPageHeight;
        /// <summary>
        /// Excel filename. When empty the workbook is written to <see cref="ResultStream"/>.
        /// </summary>
        public string FileName;
        /// <summary>
        /// Set this property to force the excel to
        /// be conatained only in one sheet
        /// </summary>
        public bool OneSheet;
        /// <summary>
        /// Gets or sets the precision factor (in twips) used to calculate column/row grid alignment.
        /// </summary>
        public int Precision;
        /// <summary>
        /// Name of the (first) worksheet. Invalid characters are replaced and it is cut to 31 characters.
        /// With several sheets the page number is appended.
        /// </summary>
        public string SheetName = "";
        /// <summary>
        /// Culture used to recognise numbers (decimal and group separators, currency symbol). Report expressions
        /// (FORMATNUM, FORMATSTR, display formats) format with the current culture, so that is the default.
        /// </summary>
        public CultureInfo Culture;
        /// <summary>
        /// Exact .NET date patterns tried, in order, to recognise a date. Empty = the short date (and date + time)
        /// patterns of <see cref="Culture"/> plus ISO (yyyy-MM-dd).
        /// </summary>
        public List<string> DatePatterns = new List<string>();
        /// <summary>
        /// Prefix an apostrophe to text that starts with = + - @ tab or CR, so no spreadsheet (or a CSV re-export of it)
        /// interprets report data as a formula. Default true.
        /// </summary>
        public bool NeutralizeFormulas = true;
        /// <summary>
        /// Digit runs without group separators longer than this stay as text (barcodes, folios, ids). Default 11.
        /// </summary>
        public int MaxPlainDigits = 11;
        /// <summary>
        /// Set column widths from the report layout (distance between consecutive column positions). Default true.
        /// </summary>
        public bool LayoutColumnWidths = true;
        /// <summary>
        /// The XLSX produced when <see cref="FileName"/> is empty.
        /// </summary>
        public MemoryStream ResultStream;
        /// <summary>
        /// Constructo and initialization
        /// </summary>
		public PrintOutClosedExcel()
            : base()
        {
            const int XLS_PRECISION = 100;
            FileName = "";
            PageQt = 0;
            FPageWidth = 11904;
            FPageHeight = 16836;
            Precision = XLS_PRECISION;
        }
        /// <summary>
        /// Draw all objects of the page to current PDF file page
        /// </summary>
        /// <param name="meta">MetaFile containing the page</param>
        /// <param name="page">MetaPage to be drawn</param>
        override public void DrawPage(MetaFile meta, MetaPage page)
        {
        }

        /// <summary>
        /// Obtain text extent
        /// </summary>
        override public Point TextExtent(TextObjectStruct aobj, Point extent)
        {
            return extent;
        }
        /// <summary>
        /// Obtain graphic extent
        /// </summary>
        /// <param name="astream">Stream containing a bitmap or a Jpeg image</param>
        /// <param name="extent">Initial bounding box</param>
        /// <param name="dpi">Resolution in Dots per inch of the image</param>
        /// <returns>Size in twips of the image</returns>
        override public Point GraphicExtent(MemoryStream astream, Point extent,
            int dpi)
        {
            return new Point(0, 0);
        }
        /// <summary>
        /// Sets page size
        /// </summary>
        /// <param name="psize">Input value</param>
        /// <returns>Size in twips of the page</returns>
        override public Point SetPageSize(PageSizeDetail psize)
        {
            int newwidth, newheight;
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
        /// Get page size
        /// </summary>
        /// <param name="indexqt">Output parameters, index for PageSizeArray</param>
        /// <returns>Size in twips of the page</returns>
        override public Point GetPageSize(out int indexqt)
        {
            indexqt = PageQt;
            return new Point(FPageWidth, FPageHeight);
        }
        /// <summary>
        /// The driver should do initialization here, a print driver should start a print document,
        /// while a preview driver should initialize a bitmap
        /// </summary>
        public override void NewDocument(MetaFile meta)
        {
        }

        // ── Cell value classification ────────────────────────────────────────────────────────────────────────────

        /// <summary>Kind of value a printed text holds.</summary>
        public enum CellKind
        {
            /// <summary>Plain text.</summary>
            Text,
            /// <summary>A number (double).</summary>
            Number,
            /// <summary>A date or date and time.</summary>
            Date
        }

        /// <summary>The value a printed text becomes in a cell.</summary>
        public struct CellValue
        {
            /// <summary>Kind of value.</summary>
            public CellKind Kind;
            /// <summary>Text (already neutralised) when <see cref="Kind"/> is Text.</summary>
            public string Text;
            /// <summary>Numeric value when <see cref="Kind"/> is Number.</summary>
            public double Number;
            /// <summary>Date value when <see cref="Kind"/> is Date.</summary>
            public DateTime Date;
            /// <summary>Excel number format that reproduces the printed text, or null for General.</summary>
            public string Format;
        }

        /// <summary>
        /// True if a spreadsheet could interpret the text as a formula (or DDE): it starts with = + - @ tab or CR.
        /// </summary>
        public static bool LooksLikeFormula(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            char c = text[0];
            return c == '=' || c == '+' || c == '-' || c == '@' || c == '\t' || c == '\r';
        }

        /// <summary>
        /// Text as it goes into a cell: an apostrophe in front when it could be read as a formula.
        /// </summary>
        public static string NeutralizeText(string text)
        {
            return LooksLikeFormula(text) ? "'" + text : text;
        }

        /// <summary>
        /// Decides what a printed text is: number, date or text (neutralised when <paramref name="neutralize"/>).
        /// </summary>
        public static CellValue Classify(string text, CultureInfo culture, IList<string> datePatterns, bool neutralize, int maxPlainDigits)
        {
            if (culture == null)
                culture = CultureInfo.CurrentCulture;
            CellValue result = new CellValue();
            string t = (text ?? "").Trim();
            if (t.Length > 0 && t.Length <= 40)
            {
                double number;
                string format;
                if (TryNumber(t, culture.NumberFormat, maxPlainDigits, out number, out format))
                {
                    result.Kind = CellKind.Number;
                    result.Number = number;
                    result.Format = format;
                    return result;
                }
                DateTime date;
                string datePattern;
                if (TryDate(t, culture, datePatterns, out date, out datePattern))
                {
                    result.Kind = CellKind.Date;
                    result.Date = date;
                    result.Format = ExcelDateFormat(datePattern);
                    return result;
                }
            }
            result.Kind = CellKind.Text;
            string s = text ?? "";
            if (s.Length > 32000)
                s = s.Substring(0, 32000);
            result.Text = neutralize ? NeutralizeText(s) : s;
            return result;
        }

        static bool TryNumber(string t, NumberFormatInfo nfi, int maxPlainDigits, out double value, out string format)
        {
            value = 0;
            format = null;
            string s = t;
            bool negative = false;
            bool parens = false;
            bool percent = false;
            string currency = null;

            if (s.Length > 2 && s[0] == '(' && s[s.Length - 1] == ')')
            {
                parens = true;
                negative = true;
                s = s.Substring(1, s.Length - 2).Trim();
            }
            if (!parens && s.Length > 1 && (s[0] == '-' || s[0] == '+' || s.StartsWith(nfi.NegativeSign, StringComparison.Ordinal)))
            {
                negative = s[0] != '+';
                s = s.Substring(s[0] == '-' || s[0] == '+' ? 1 : nfi.NegativeSign.Length).Trim();
            }
            // Currency symbol before or after: the culture's and the dollar sign (the invariant culture uses ¤).
            foreach (string sym in new string[] { nfi.CurrencySymbol, "$" })
            {
                if (string.IsNullOrEmpty(sym) || currency != null)
                    continue;
                if (s.StartsWith(sym, StringComparison.Ordinal))
                {
                    currency = sym;
                    s = s.Substring(sym.Length).Trim();
                }
                else if (s.EndsWith(sym, StringComparison.Ordinal))
                {
                    currency = sym;
                    s = s.Substring(0, s.Length - sym.Length).Trim();
                }
            }
            // A sign after the currency symbol: "$-1.00".
            if (!negative && currency != null && s.Length > 1 && s[0] == '-')
            {
                negative = true;
                s = s.Substring(1).Trim();
            }
            if (s.EndsWith("%", StringComparison.Ordinal) && currency == null)
            {
                percent = true;
                s = s.Substring(0, s.Length - 1).Trim();
            }
            if (s.Length == 0)
                return false;

            string dec = nfi.NumberDecimalSeparator;
            string grp = nfi.NumberGroupSeparator;
            if (currency != null && currency == nfi.CurrencySymbol)
            {
                dec = nfi.CurrencyDecimalSeparator;
                grp = nfi.CurrencyGroupSeparator;
            }
            // Non-breaking spaces as group separator (fr, es-CR…) are printed as a normal space by some formats.
            string grpPattern = (grp == " " || grp == " " || grp == " ") ? "[   ]" : Regex.Escape(grp ?? ",");
            string decPattern = Regex.Escape(dec ?? ".");
            Match m = Regex.Match(s, "^(?<int>\\d{1,3}(?:" + grpPattern + "\\d{3})+|\\d+)(?:" + decPattern + "(?<dec>\\d+))?$");
            if (!m.Success)
                return false;
            string intPart = m.Groups["int"].Value;
            string decPart = m.Groups["dec"].Success ? m.Groups["dec"].Value : "";
            bool grouped = intPart.Length > 0 && !Regex.IsMatch(intPart, "^\\d+$");
            string digits = Regex.Replace(intPart, "\\D", "");
            // Codes are not numbers: leading zeros ("00123") and long plain digit runs (barcodes, folios) stay text.
            if (!grouped && decPart.Length == 0 && !percent && currency == null)
            {
                if (digits.Length > 1 && digits[0] == '0')
                    return false;
                if (digits.Length > maxPlainDigits)
                    return false;
            }
            if ((digits.TrimStart('0') + decPart).Length > 15)
                return false; // beyond double precision: keep the exact text
            double v;
            if (!double.TryParse(digits + (decPart.Length > 0 ? "." + decPart : ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v))
                return false;
            if (negative)
                v = -v;
            if (percent)
                v = v / 100;

            // Below 1000 grouping is invisible, so "99.90" next to "1,234.50" gets the same #,##0.00 (a column keeps one format).
            bool groupFormat = grouped || (decPart.Length > 0 && digits.TrimStart('0').Length <= 3);
            string body = (groupFormat ? "#,##0" : "0") + (decPart.Length > 0 ? "." + new string('0', decPart.Length) : "");
            if (percent)
                format = body + "%";
            else if (currency != null)
                format = "\"" + currency.Replace("\"", "") + "\"" + body;
            else if (grouped || decPart.Length > 0)
                format = body;
            if (parens)
                format = (format ?? body) + ";(" + (format ?? body) + ")";
            value = v;
            return true;
        }

        static bool TryDate(string t, CultureInfo culture, IList<string> patterns, out DateTime date, out string pattern)
        {
            date = DateTime.MinValue;
            pattern = null;
            if (t.Length < 6 || t.Length > 22 || !char.IsDigit(t[0]))
                return false;
            IList<string> list = (patterns != null && patterns.Count > 0) ? patterns : DefaultDatePatterns(culture);
            foreach (string p in list)
            {
                if (DateTime.TryParseExact(t, p, culture, DateTimeStyles.AllowWhiteSpaces, out date))
                {
                    if (date.Year < 1800 || date.Year >= 9000)
                        return false;
                    pattern = p;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Short date, short date + short/long time and ISO patterns of a culture.
        /// </summary>
        public static List<string> DefaultDatePatterns(CultureInfo culture)
        {
            DateTimeFormatInfo d = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat;
            List<string> l = new List<string>();
            foreach (string p in new string[] {
                d.ShortDatePattern + " " + d.LongTimePattern, d.ShortDatePattern + " " + d.ShortTimePattern, d.ShortDatePattern,
                "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" })
                if (!l.Contains(p))
                    l.Add(p);
            return l;
        }

        /// <summary>
        /// Converts a .NET date pattern to an Excel number format (dd/MM/yyyy HH:mm → dd/mm/yyyy hh:mm).
        /// </summary>
        public static string ExcelDateFormat(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return "dd/mm/yyyy";
            StringBuilder sb = new StringBuilder();
            bool hasAmPm = pattern.Contains("t");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                int run = 1;
                while (i + run < pattern.Length && pattern[i + run] == c)
                    run++;
                string token = new string(c, run);
                switch (c)
                {
                    case 'M': sb.Append(token.ToLowerInvariant()); break;          // month → m (Excel decides by context)
                    case 'm': sb.Append(run == 1 ? "m" : "mm"); break;             // minutes
                    case 'H': sb.Append(run == 1 ? "h" : "hh"); break;
                    case 'h': sb.Append(run == 1 ? "h" : "hh"); break;
                    case 't': sb.Append("AM/PM"); break;
                    case 'f': case 'F': case 'z': case 'K': break;
                    case '\'':
                        {
                            int end = pattern.IndexOf('\'', i + 1);
                            if (end < 0) end = pattern.Length;
                            sb.Append('"').Append(pattern.Substring(i + 1, end - i - 1)).Append('"');
                            run = end - i + 1;
                            break;
                        }
                    default: sb.Append(token); break;
                }
                i += run - 1;
            }
            string r = sb.ToString();
            if (!hasAmPm)
                r = r.Replace("AM/PM", "");
            return r.Trim();
        }

        /// <summary>
        /// Sheet names: no [ ] : * ? / \, not empty, at most 31 characters.
        /// </summary>
        public static string SafeSheetName(string name)
        {
            string s = Regex.Replace(name ?? "", "[\\[\\]:*?/\\\\]", "_").Trim().Trim('\'');
            if (s.Length == 0)
                s = "Report";
            if (s.Length > 31)
                s = s.Substring(0, 31);
            return s;
        }

        /// <summary>
        /// Renders a single report meta-object (text or image) into the corresponding worksheet cell (current culture, defaults).
        /// </summary>
        public static void PrintObject(ClosedXML.Excel.IXLWorksheet sh, MetaPage page, MetaObject obj, int dpix,
             int dpiy, SortedList rows, SortedList columns,
             string FontName, int FontSize, int rowinit, double Precision)
        {
            PrintObject(sh, page, obj, rows, columns, FontName, FontSize, rowinit, Precision,
                CultureInfo.CurrentCulture, null, true, 11);
        }

        /// <summary>
        /// Renders a single report meta-object (text or image) into the corresponding worksheet cell.
        /// </summary>
        public static void PrintObject(ClosedXML.Excel.IXLWorksheet sh, MetaPage page, MetaObject obj,
             SortedList rows, SortedList columns, string FontName, int FontSize, int rowinit, double Precision,
             CultureInfo culture, IList<string> datePatterns, bool neutralize, int maxPlainDigits)
        {
            string topstring = ((double)obj.Top / Precision).ToString("0000000000");
            string leftstring = ((double)obj.Left / Precision).ToString("0000000000");
            int arow = rows.IndexOfKey(topstring) + 1 + rowinit;
            int acolumn = columns.IndexOfKey(leftstring) + 1;
            if (acolumn < 1)
                acolumn = 1;
            if (arow < 1)
                arow = 1;

            var cell = sh.Cell(arow, acolumn);

            switch (obj.MetaType)
            {
                case MetaObjectType.Image:
                    MetaObjectImage obji = (MetaObjectImage)obj;
                    try
                    {
                        using (MemoryStream mstream = page.GetStream(obji))
                        {
                            var image = sh.AddPicture(mstream);
                            image.MoveTo(cell);
                        }
                    }
                    catch (Exception)
                    {
                        // A format ClosedXML cannot read (or an empty stream) must not abort the whole export.
                    }
                    break;
                case MetaObjectType.Text:
                    MetaObjectText objt = (MetaObjectText)obj;
                    string atext = page.GetText(objt);
                    if (string.IsNullOrEmpty(atext))
                        break;
                    // Two objects on the same cell (same row and column in the grid): keep both texts.
                    if (!cell.IsEmpty())
                    {
                        string previous = cell.GetFormattedString();
                        atext = previous + " " + atext;
                        cell.Clear(ClosedXML.Excel.XLClearOptions.Contents | ClosedXML.Excel.XLClearOptions.NormalFormats);
                    }
                    CellValue cv = Classify(atext, culture, datePatterns, neutralize, maxPlainDigits);
                    bool isanumber = cv.Kind != CellKind.Text;
                    // Values are always typed: text is set as text, so it can never become a formula.
                    switch (cv.Kind)
                    {
                        case CellKind.Number:
                            cell.Value = cv.Number;
                            if (cv.Format != null)
                                cell.Style.NumberFormat.Format = cv.Format;
                            break;
                        case CellKind.Date:
                            cell.Value = cv.Date;
                            if (cv.Format != null)
                                cell.Style.NumberFormat.Format = cv.Format;
                            break;
                        default:
                            // ClosedXML takes a leading apostrophe as Excel's quote prefix and DROPS it from the text: a
                            // neutralised "'=X" would be stored as "=X" (only the style flag left) and a legitimate "'abc"
                            // would lose its apostrophe. Doubling it keeps the literal apostrophe in the value (safe even if
                            // the sheet is re-exported to CSV) and the quote-prefix flag on the cell.
                            string ctext = cv.Text;
                            if (ctext.Length > 0 && ctext[0] == '\'')
                                ctext = "'" + ctext;
                            cell.Value = ctext;
                            break;
                    }

                    string nfontname = page.GetWFontNameText(objt);
                    if (!string.IsNullOrEmpty(nfontname) && FontName != nfontname)
                        cell.Style.Font.FontName = nfontname;
                    if (objt.FontSize > 0 && objt.FontSize != FontSize)
                        cell.Style.Font.FontSize = objt.FontSize;
                    Color acolor = GraphicUtils.ColorFromInteger(objt.FontColor);
                    if (acolor.ToArgb() != Color.Black.ToArgb())
                        cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromArgb(acolor.R, acolor.G, acolor.B);
                    if (GraphicUtils.FontStyleIsItalic(objt.FontStyle))
                        cell.Style.Font.Italic = true;
                    if (GraphicUtils.FontStyleIsBold(objt.FontStyle))
                        cell.Style.Font.Bold = true;
                    if (GraphicUtils.FontStyleIsUnderline(objt.FontStyle))
                        cell.Style.Font.SetUnderline(ClosedXML.Excel.XLFontUnderlineValues.Single);
                    if (GraphicUtils.FontStyleIsStrikeOut(objt.FontStyle))
                        cell.Style.Font.SetStrikethrough(true);
                    if ((objt.Alignment & MetaFile.AlignmentFlags_AlignHCenter) > 0)
                        cell.Style.Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Center);
                    if ((objt.Alignment & MetaFile.AlignmentFlags_AlignLeft) > 0 && isanumber)
                        cell.Style.Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Left);
                    if ((objt.Alignment & MetaFile.AlignmentFlags_AlignRight) > 0 && !isanumber)
                        cell.Style.Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Right);
                    if (objt.WordWrap)
                        cell.Style.Alignment.SetWrapText(true);
                    break;
            }
        }

        /// <summary>
        /// The driver should do cleanup here, a print driver should finish print document.
        /// </summary>
        public override void EndDocument(MetaFile meta)
        {
        }
        /// <summary>
        /// The driver should start a new page
        /// </summary>
        public override void NewPage(MetaFile meta, MetaPage page)
        {
        }

        /// <summary>
        /// Checks whether enough time has elapsed since the last progress notification and,
        /// if so, raises the metafile work-progress callback. Throws if the callback signals cancellation.
        /// </summary>
        /// <param name="finished">If <c>true</c>, forces the progress check regardless of the elapsed time.</param>
        protected void CheckProgress(bool finished)
        {
            const int MILIS_PROGRESS_DEFAULT = 500;

            DateTime mmlast = System.DateTime.Now;
            TimeSpan difmilis = mmlast - mmfirst;
            if ((difmilis.TotalMilliseconds >= MILIS_PROGRESS_DEFAULT) || finished)
            {
                mmfirst = System.DateTime.Now;
                bool docancel = false;
                nmeta.WorkProgress(nrecord, npage, ref docancel);
                if (docancel)
                    throw new UnNamedException(Translator.TranslateStr(503));
            }
        }

        string SheetTitle(int number, bool several)
        {
            if (string.IsNullOrWhiteSpace(SheetName))
                return "Page " + number.ToString();
            string suffix = several ? " " + number.ToString() : "";
            string b = SafeSheetName(SheetName);
            if (b.Length + suffix.Length > 31)
                b = b.Substring(0, 31 - suffix.Length);
            return b + suffix;
        }

        /// <summary>
        /// Generate the excel file (or <see cref="ResultStream"/> when <see cref="FileName"/> is empty)
        /// </summary>
        /// <param name="meta"></param>
        override public bool Print(MetaFile meta)
        {
            npage = 0;
            nrecord = 0;
            nmeta = meta;
            mmfirst = System.DateTime.Now;
            CultureInfo culture = Culture ?? CultureInfo.CurrentCulture;
            bool aresult = base.Print(meta);
            int PageLimit = ToPage - 1;
            int FirstPage = FromPage - 1;
            meta.RequestPage(int.MaxValue - 1);
            if (meta.Pages.CurrentCount <= FirstPage)
                return false;
            if (ToPage > (meta.Pages.CurrentCount - 1))
                PageLimit = meta.Pages.CurrentCount - 1;
            bool several = !OneSheet && PageLimit > FirstPage;

            using (ClosedXML.Excel.XLWorkbook wb = new ClosedXML.Excel.XLWorkbook())
            {
                int shcount = 1;
                ClosedXML.Excel.IXLWorksheet sh = wb.AddWorksheet(SheetTitle(shcount, several));
                var font = sh.Style.Font;
                string FontName = font.FontName;
                double FontSize = font.FontSize;

                SetPageSize(meta.Pages[0].PageDetail);
                SetOrientation(meta.Orientation);

                SortedList columns = new SortedList();
                SortedList rows = new SortedList();
                // Widest object starting at each column position (twips), for the last column's width.
                Dictionary<string, int> widths = new Dictionary<string, int>();
                MetaPage apage;
                int i, index;
                // First pass to determine columns
                for (i = FirstPage; i <= PageLimit; i++)
                {
                    apage = meta.Pages[i];
                    foreach (MetaObject obj1 in apage.Objects)
                    {
                        if ((obj1.MetaType == MetaObjectType.Text) || (obj1.MetaType == MetaObjectType.Image))
                        {
                            string leftstring = ((double)obj1.Left / Precision).ToString("0000000000");
                            index = columns.IndexOfKey(leftstring);
                            if (index < 0)
                                columns.Add(leftstring, obj1.Left);
                            int w;
                            if (!widths.TryGetValue(leftstring, out w) || obj1.Width > w)
                                widths[leftstring] = obj1.Width;
                        }
                    }
                }
                int rowinit = 0;
                List<ClosedXML.Excel.IXLWorksheet> sheets = new List<ClosedXML.Excel.IXLWorksheet>();
                sheets.Add(sh);
                // Second pass determine rows
                for (i = FirstPage; i <= PageLimit; i++)
                {
                    npage = i;
                    if (!OneSheet)
                    {
                        rowinit = 0;
                        if (wb.Worksheets.Count < shcount)
                        {
                            sh = wb.Worksheets.Add(SheetTitle(shcount, several));
                            sheets.Add(sh);
                        }
                        else
                            sh = wb.Worksheets.Worksheet(shcount);
                    }
                    else
                    {
                        rowinit = rowinit + rows.Count;
                    }

                    shcount++;
                    rows.Clear();

                    apage = meta.Pages[i];
                    nrecord = 0;
                    foreach (MetaObject obj2 in apage.Objects)
                    {
                        if ((obj2.MetaType == MetaObjectType.Text) || (obj2.MetaType == MetaObjectType.Image))
                        {
                            string topstring = ((double)obj2.Top / Precision).ToString("0000000000");
                            index = rows.IndexOfKey(topstring);
                            if (index < 0)
                                rows.Add(topstring, null);
                        }
                    }
                    // Finally, draw objects
                    foreach (MetaObject obj in apage.Objects)
                    {
                        if ((obj.MetaType == MetaObjectType.Text) || (obj.MetaType == MetaObjectType.Image))
                        {
                            PrintObject(sh, apage, obj, rows, columns, FontName, Convert.ToInt32(FontSize), rowinit, Precision,
                                culture, DatePatterns, NeutralizeFormulas, MaxPlainDigits);
                        }
                        nrecord++;
                        CheckProgress(false);
                    }
                }
                if (LayoutColumnWidths && columns.Count > 0)
                {
                    // Excel width unit ≈ one '0' of the default font ≈ 7 px at 96 dpi = 105 twips.
                    const double TWIPS_PER_CHAR = 105.0;
                    for (int c = 0; c < columns.Count; c++)
                    {
                        int left = (int)columns.GetByIndex(c);
                        int span = c + 1 < columns.Count
                            ? (int)columns.GetByIndex(c + 1) - left
                            : widths[(string)columns.GetKey(c)];
                        double chars = Math.Max(1.0, Math.Min(100.0, span / TWIPS_PER_CHAR));
                        foreach (var s in sheets)
                            s.Column(c + 1).Width = Math.Round(chars, 2);
                    }
                }
                EndDocument(meta);
                CheckProgress(true);

                if (FileName.Length > 0)
                {
                    wb.SaveAs(FileName);
                }
                else
                {
                    ResultStream = new MemoryStream();
                    wb.SaveAs(ResultStream);
                    ResultStream.Position = 0;
                }
            }
            return aresult;
        }

    }
}
