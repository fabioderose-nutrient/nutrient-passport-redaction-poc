using GdPicture14;
using PassportRedaction.Models;

namespace PassportRedaction.Services;

public class PassportRedactionService : IDisposable
{
    private readonly string _resourceFolder;
    private readonly ILogger<PassportRedactionService> _logger;

    public PassportRedactionService(IConfiguration config, ILogger<PassportRedactionService> logger)
    {
        _logger = logger;

        // GdPicture OCR resource folder (contains ocrb.traineddata for MRZ)
        // When published with -r <rid>, resources are flattened to the output root.
        // When running via `dotnet run`, they're under runtimes/any/native/.
        var publishPath = AppContext.BaseDirectory;
        var runtimePath = Path.Combine(publishPath, "runtimes", "any", "native");
        var defaultPath = File.Exists(Path.Combine(publishPath, "ocrb.traineddata"))
            ? publishPath
            : runtimePath;

        // GdPicture:ResourceFolder (environment variable GdPicture__ResourceFolder) overrides it.
        _resourceFolder = config.GetValue<string>("GdPicture:ResourceFolder") is { Length: > 0 } folder
            ? folder
            : defaultPath;
        logger.LogInformation("OCR ResourceFolder: {Path}", _resourceFolder);
    }

    public RedactionResponse RedactPassport(string inputPath, string outputPath, List<string> fieldsToRedact, string docType = "td3")
    {
        using var gdpictureImaging = new GdPictureImaging();
        using var outputPdf = new GdPicturePDF();
        outputPdf.NewPDF();

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        var perPageResults = new List<(int Page, bool MrzFound, MrzInfo? Mrz)>();

        if (ext == ".pdf")
        {
            using var inputPdf = new GdPicturePDF();
            var loadStatus = inputPdf.LoadFromFile(inputPath, false);
            if (loadStatus != GdPictureStatus.OK)
                return new RedactionResponse { Success = false, Message = $"Failed to load PDF: {loadStatus}" };

            int pageCount = inputPdf.GetPageCount();
            _logger.LogInformation("PDF has {Count} page(s) — processing each", pageCount);

            for (int p = 1; p <= pageCount; p++)
            {
                inputPdf.SelectPage(p);
                int imageId = inputPdf.RenderPageToGdPictureImage(200f, true, 1.0);
                if (imageId == 0)
                {
                    _logger.LogWarning("Failed to render PDF page {P}: {Status} — skipping", p, inputPdf.GetStat());
                    continue;
                }
                try
                {
                    var (mrzFound, mrz, _, _) = ProcessSingleImage(gdpictureImaging, imageId, fieldsToRedact, p, docType);
                    perPageResults.Add((p, mrzFound, mrz));
                    AppendImageAsPage(outputPdf, gdpictureImaging, imageId);
                }
                finally
                {
                    gdpictureImaging.ReleaseGdPictureImage(imageId);
                }
            }
            inputPdf.CloseDocument();
        }
        else
        {
            int imageId = gdpictureImaging.CreateGdPictureImageFromFile(inputPath);
            if (imageId == 0)
                return new RedactionResponse { Success = false, Message = $"Failed to load document: {gdpictureImaging.GetStat()}" };

            try
            {
                var (mrzFound, mrz, _, _) = ProcessSingleImage(gdpictureImaging, imageId, fieldsToRedact, 1, docType);
                perPageResults.Add((1, mrzFound, mrz));
                AppendImageAsPage(outputPdf, gdpictureImaging, imageId);
            }
            finally
            {
                gdpictureImaging.ReleaseGdPictureImage(imageId);
            }
        }

        // No page had MRZ → not a passport
        if (!perPageResults.Any(r => r.MrzFound))
        {
            return new RedactionResponse
            {
                Success = false,
                Message = "The document hasn't been recognized (no passport MRZ or Aadhaar QR found)."
            };
        }

        // Save the multi-page output PDF
        var saveStatus = outputPdf.SaveToFile(outputPath);
        outputPdf.CloseDocument();
        if (saveStatus != GdPictureStatus.OK)
            return new RedactionResponse { Success = false, Message = $"Failed to save redacted PDF: {saveStatus}" };

        var mrzPages = perPageResults.Where(r => r.MrzFound).Select(r => r.Page).ToList();
        var firstMrz = perPageResults.FirstOrDefault(r => r.MrzFound).Mrz;
        _logger.LogInformation("Redacted {Total} page(s); MRZ found on pages: {Pages}",
            perPageResults.Count, string.Join(",", mrzPages));

        return new RedactionResponse
        {
            Success = true,
            Message = $"Redaction complete. MRZ detected on {mrzPages.Count} of {perPageResults.Count} page(s).",
            RedactedFileName = Path.GetFileName(outputPath),
            MrzData = firstMrz
        };
    }

    /// <summary>
    /// Same pipeline as RedactPassport but does NOT draw redactions. Saves the
    /// processed (deskewed/rotated/upscaled) document and returns the redaction
    /// rectangles in PDF point coordinates so the viewer can place moveable
    /// RedactionAnnotation markers.
    /// </summary>
    public RedactionPreviewResponse PreviewRedaction(string inputPath, string outputPath, List<string> fieldsToRedact, string docType = "td3")
    {
        using var gdpictureImaging = new GdPictureImaging();
        using var outputPdf = new GdPicturePDF();
        outputPdf.NewPDF();

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        var redactionRects = new List<RedactionRect>();
        bool anyMrzFound = false;
        MrzInfo? firstMrz = null;
        List<QrField>? firstQr = null;
        int totalPages = 0;

        void ProcessPage(int imageId, int pageNum)
        {
            var (mrzFound, mrz, rects, qrData) = ProcessSingleImage(gdpictureImaging, imageId, fieldsToRedact, pageNum, docType, drawRedactions: false);
            if (mrzFound && firstMrz == null) firstMrz = mrz;
            if (qrData != null && firstQr == null) firstQr = qrData;
            anyMrzFound |= mrzFound;

            // Image dimensions AFTER all transformations (used to convert to PDF points)
            int imgW = gdpictureImaging.GetWidth(imageId);
            int imgH = gdpictureImaging.GetHeight(imageId);
            float hRes = gdpictureImaging.GetHorizontalResolution(imageId);
            float vRes = gdpictureImaging.GetVerticalResolution(imageId);
            if (hRes <= 0) hRes = 200;
            if (vRes <= 0) vRes = 200;

            // PDF page dimensions in points (top-left origin, used by PSPDFKit)
            float pageWPt = imgW / hRes * 72f;
            float pageHPt = imgH / vRes * 72f;
            float pxToPtX = pageWPt / imgW;
            float pxToPtY = pageHPt / imgH;

            int outPageIndex = totalPages; // 0-based for the new PDF
            foreach (var (field, x, y, w, h) in rects)
            {
                redactionRects.Add(new RedactionRect
                {
                    Field = field,
                    PageIndex = outPageIndex,
                    X = Math.Max(0, x) * pxToPtX,
                    Y = Math.Max(0, y) * pxToPtY,
                    Width = w * pxToPtX,
                    Height = h * pxToPtY,
                });
            }

            AppendImageAsPage(outputPdf, gdpictureImaging, imageId);
            totalPages++;
        }

        if (ext == ".pdf")
        {
            using var inputPdf = new GdPicturePDF();
            var loadStatus = inputPdf.LoadFromFile(inputPath, false);
            if (loadStatus != GdPictureStatus.OK)
                return new RedactionPreviewResponse { Success = false, Message = $"Failed to load PDF: {loadStatus}" };

            int pageCount = inputPdf.GetPageCount();
            for (int p = 1; p <= pageCount; p++)
            {
                inputPdf.SelectPage(p);
                int imageId = inputPdf.RenderPageToGdPictureImage(200f, true, 1.0);
                if (imageId == 0) continue;
                try { ProcessPage(imageId, p); }
                finally { gdpictureImaging.ReleaseGdPictureImage(imageId); }
            }
            inputPdf.CloseDocument();
        }
        else
        {
            int imageId = gdpictureImaging.CreateGdPictureImageFromFile(inputPath);
            if (imageId == 0)
                return new RedactionPreviewResponse { Success = false, Message = $"Failed to load document: {gdpictureImaging.GetStat()}" };
            try { ProcessPage(imageId, 1); }
            finally { gdpictureImaging.ReleaseGdPictureImage(imageId); }
        }

        if (!anyMrzFound)
            return new RedactionPreviewResponse { Success = false, Message = "The document hasn't been recognized (no passport MRZ or Aadhaar QR found)." };

        var saveStatus = outputPdf.SaveToFile(outputPath);
        outputPdf.CloseDocument();
        if (saveStatus != GdPictureStatus.OK)
            return new RedactionPreviewResponse { Success = false, Message = $"Failed to save preview PDF: {saveStatus}" };

        return new RedactionPreviewResponse
        {
            Success = true,
            Message = $"Found {redactionRects.Count} redaction zones across {totalPages} page(s).",
            PreviewDocumentUrl = $"/output-docs/{Path.GetFileName(outputPath)}",
            Redactions = redactionRects,
            MrzData = firstMrz,
            QrFormat = firstQr != null ? (docType.Equals("aadhaar", StringComparison.OrdinalIgnoreCase) ? "Aadhaar QR" : "QR") : null,
            QrData = firstQr,
        };
    }


    /// <summary>
    /// Runs the full deskew → MRZ detection → full-page OCR → label-based redaction pipeline
    /// on a single in-memory image. Returns whether MRZ was detected and the parsed MRZ data.
    /// If MRZ is not found, the image is left untouched (the page is kept unchanged in the output).
    /// </summary>
    private (bool MrzFound, MrzInfo? Mrz, List<(string Field, float X, float Y, float W, float H)> Rects, List<QrField>? QrData)
        ProcessSingleImage(
            GdPictureImaging gdpictureImaging, int imageId, List<string> fieldsToRedact, int pageNum,
            string docType = "td3", bool drawRedactions = true)
    {
        // Aadhaar has NO MRZ — it uses a QR-anchored pipeline instead of the
        // MRZ-anchored passport pipeline below.
        if (string.Equals(docType, "aadhaar", StringComparison.OrdinalIgnoreCase))
            return ProcessAadhaarImage(gdpictureImaging, imageId, fieldsToRedact, pageNum, drawRedactions);

        var rectsOut = new List<(string Field, float X, float Y, float W, float H)>();
        using var gdpictureOCR = new GdPictureOCR();
        gdpictureOCR.ResourceFolder = _resourceFolder;

        try
        {
            // --- Apply EXIF rotation so raw pixels match how the user sees the image ---
            // Many phone-shot images have EXIF orientation tag; viewers honor it but
            // our raw pixel processing doesn't. Bake it into the pixels first.
            var exifRot = gdpictureImaging.TagGetExifRotation(imageId);
            if (exifRot != GdPictureRotateFlipType.GdPictureRotateNoneFlipNone)
            {
                _logger.LogInformation("[page {P}] Applying EXIF rotation: {Rot}", pageNum, exifRot);
                gdpictureImaging.ApplyExifRotation(imageId);
            }

            // --- Upscale small images so MRZ OCR has enough pixels ---
            // PDFs are rendered at 200 DPI which gives plenty of resolution,
            // but raw images can be tiny (e.g. 265x409). MRZ characters need
            // at least ~25px tall for reliable OCR, which means the full image
            // should be at least ~1800px on the long edge.
            int curW = gdpictureImaging.GetWidth(imageId);
            int curH = gdpictureImaging.GetHeight(imageId);
            const int minLongEdge = 1800;
            int longEdge = Math.Max(curW, curH);
            if (longEdge < minLongEdge)
            {
                float scale = (float)minLongEdge / longEdge;
                int newW = (int)(curW * scale);
                int newH = (int)(curH * scale);
                _logger.LogInformation("[page {P}] Upscaling {OldW}x{OldH} → {NewW}x{NewH} (×{Scale:F2}) for OCR",
                    pageNum, curW, curH, newW, newH, scale);
                gdpictureImaging.Resize(imageId, newW, newH, GdPicture14.Imaging.GdPictureInterpolationMode.HighQualityBicubic);
            }

            // --- Deskew ---
            _logger.LogInformation("[page {P}] Deskewing image...", pageNum);
            gdpictureImaging.AutoDeskew(imageId);

            // --- MRZ OCR — try all 4 orientations until MRZ is found ---
            // Some scans/photos are rotated 90/180/270°. AutoDeskew only fixes
            // small angles, so we rotate explicitly and retry. We track the total
            // rotation applied so we can undo it after redaction (so the output
            // keeps the same orientation as the input).
            string mrzResultId = "";
            bool mrzOk = false;
            int rotationApplied = 0; // cumulative degrees clockwise

            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                {
                    // Rotate counter-clockwise (90° CCW = 270° CW)
                    gdpictureImaging.Rotate(imageId, GdPictureRotateFlipType.GdPictureRotate270FlipNone);
                    rotationApplied = (rotationApplied + 270) % 360;
                }

                gdpictureOCR.ReleaseOCRResults();
                gdpictureOCR.SetImage(imageId);
                mrzResultId = gdpictureOCR.RunOCR(OCRSpecialContext.MRZ);
                var stat = gdpictureOCR.GetStat();

                if (stat == GdPictureStatus.OK && gdpictureOCR.GetBlockCount(mrzResultId) > 0)
                {
                    var txt = gdpictureOCR.GetOCRResultText(mrzResultId) ?? "";
                    if (LooksLikeRealMrz(txt))
                    {
                        _logger.LogInformation("[page {P}] MRZ found at rotation {Deg}°: [{Txt}]",
                            pageNum, rotationApplied, txt.Replace("\n", " | "));
                        mrzOk = true;
                        break;
                    }
                    _logger.LogInformation("[page {P}] OCR returned text but not real MRZ: [{Txt}]",
                        pageNum, txt.Replace("\n", " | "));
                }
                _logger.LogInformation("[page {P}] No MRZ at rotation {Deg}°", pageNum, rotationApplied);
            }

            if (!mrzOk)
            {
                _logger.LogInformation("[page {P}] No MRZ in any rotation — leaving page unchanged", pageNum);
                return (false, null, rectsOut, null);
            }

            // --- Now that the image is upright, deskew it again (handles small
            //     residual angles that AutoDeskew can't fix on rotated content) ---
            _logger.LogInformation("[page {P}] Deskewing post-rotation...", pageNum);
            gdpictureImaging.AutoDeskew(imageId);

            // --- Re-detect MRZ on the deskewed image to get fresh coordinates ---
            gdpictureOCR.ReleaseOCRResults();
            gdpictureOCR.SetImage(imageId);
            mrzResultId = gdpictureOCR.RunOCR(OCRSpecialContext.MRZ);
            if (gdpictureOCR.GetStat() != GdPictureStatus.OK || gdpictureOCR.GetBlockCount(mrzResultId) == 0)
            {
                _logger.LogWarning("[page {P}] MRZ lost after post-rotation deskew — keeping pre-deskew result");
                // Re-run on the (skewed) image since the coords above are now stale
                gdpictureOCR.ReleaseOCRResults();
                gdpictureOCR.SetImage(imageId);
                mrzResultId = gdpictureOCR.RunOCR(OCRSpecialContext.MRZ);
            }

            string mrzText = gdpictureOCR.GetOCRResultText(mrzResultId) ?? "";
            var mrzTextLines = mrzText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (mrzTextLines.Length < 2 || mrzTextLines[0].Length < 30)
            {
                _logger.LogInformation("[page {P}] MRZ text too short ({Len}) — leaving page unchanged", pageNum, mrzText.Length);
                return (false, null, rectsOut, null);
            }

            var mrzData = ParseMrz(mrzTextLines);
            int mrzLeft = gdpictureOCR.GetBlockLeft(mrzResultId, 0);
            int mrzTop = gdpictureOCR.GetBlockTop(mrzResultId, 0);
            int mrzRight = gdpictureOCR.GetBlockRight(mrzResultId, 0);
            int mrzBottom = gdpictureOCR.GetBlockBottom(mrzResultId, 0);
            int mrzWidth = mrzRight - mrzLeft;
            if (mrzWidth <= 0) return (false, null, rectsOut, null);

            float cw = mrzWidth / 44.0f;
            _logger.LogInformation("[page {P}] MRZ ({L},{T})-({R},{B}), cw={CW}",
                pageNum, mrzLeft, mrzTop, mrzRight, mrzBottom, cw);

            // --- Full English OCR ---
            gdpictureOCR.ReleaseOCRResults();
            gdpictureOCR.ResetROI();
            gdpictureOCR.SetImage(imageId);
            gdpictureOCR.AddLanguage(OCRLanguage.English);
            string fullResultId = gdpictureOCR.RunOCR();

            var lines = new List<OcrLine>();
            if (gdpictureOCR.GetStat() == GdPictureStatus.OK)
            {
                int lineCount = gdpictureOCR.GetTextLineCount(fullResultId);
                for (int i = 0; i < lineCount; i++)
                {
                    lines.Add(new OcrLine
                    {
                        Text = gdpictureOCR.GetTextLineValue(fullResultId, i) ?? "",
                        Left = gdpictureOCR.GetTextLineLeft(fullResultId, i),
                        Top = gdpictureOCR.GetTextLineTop(fullResultId, i),
                        Right = gdpictureOCR.GetTextLineRight(fullResultId, i),
                        Bottom = gdpictureOCR.GetTextLineBottom(fullResultId, i),
                    });
                }
                _logger.LogInformation("[page {P}] Full OCR found {Count} text lines", pageNum, lines.Count);
                foreach (var l in lines)
                    _logger.LogInformation("[page {P}]   line @({X},{Y}) {W}x{H}: \"{T}\"",
                        pageNum, l.Left, l.Top, l.Width, l.Height, l.Text);
            }

            // --- Detect the data column left edge — the X where most labels/values start.
            //     The photo sits to the LEFT of this; data fields sit to the RIGHT. ---
            int dataColumnLeft = DetectDataColumnLeft(lines, mrzLeft, mrzRight, mrzTop)
                ?? mrzLeft + (int)(cw * 14f); // fallback: ~middle of MRZ
            _logger.LogInformation("[page {P}] Data column left = {X}", pageNum, dataColumnLeft);

            // --- Compute (and optionally apply) redaction rects ---
            int blackColor = gdpictureImaging.ARGBI(255, 0, 0, 0);
            foreach (var field in fieldsToRedact)
            {
                var rect = ResolveFieldRectForDocType(docType, field, lines, mrzData, mrzLeft, mrzTop, mrzRight, mrzBottom, cw, dataColumnLeft);
                if (rect == null) continue;
                var (fx, fy, fw, fh) = rect.Value;
                rectsOut.Add((field, fx, fy, fw, fh));
                if (drawRedactions)
                {
                    gdpictureImaging.DrawFilledRectangle(imageId,
                        Math.Max(0, fx), Math.Max(0, fy), fw, fh, blackColor, false);
                }
            }

            return (true, mrzData, rectsOut, null);
        }
        finally
        {
            gdpictureOCR.ReleaseOCRResults();
        }
    }

    // =====================================================================
    // Aadhaar pipeline (QR-anchored)
    // =====================================================================
    // Aadhaar cards carry no MRZ but always have a QR code on the front.
    // We detect the QR via GdPicture's BarcodeQRReader, take its bounding
    // box as the anchor, and lay out the redaction rectangles for the
    // personal-data column (Name / DOB / Gender / Aadhaar number) and the
    // photo as vectors expressed in units of the QR side length `q`.
    // Offsets follow the standard front-side card layout:
    //
    //   ┌───────────────────────────────────────────────┐
    //   │              ISSUER HEADER                    │
    //   │  ┌────────┐   Name : XXXXX                      │
    //   │  │        │   DOB  : DD/MM/YYYY    ┌─────────┐  │
    //   │  │ PHOTO  │   Gender : MALE        │   QR    │  │
    //   │  │        │   XXXX XXXX XXXX       └─────────┘  │
    //   │  └────────┘                                     │
    //   └───────────────────────────────────────────────┘
    //         ↑ photo        ↑ data column      ↑ QR anchor
    //
    // The 12-digit number is additionally refined via OCR (it is exactly
    // 12 digits, so it is unambiguous to locate) and falls back to the
    // QR-relative box if OCR misses it. Markers are draggable in the UI.
    private (bool Detected, MrzInfo? Mrz, List<(string Field, float X, float Y, float W, float H)> Rects, List<QrField>? QrData)
        ProcessAadhaarImage(GdPictureImaging img, int imageId, List<string> fieldsToRedact, int pageNum, bool drawRedactions)
    {
        var rectsOut = new List<(string Field, float X, float Y, float W, float H)>();

        // --- Bake EXIF rotation into the pixels (phone photos) ---
        var exifRot = img.TagGetExifRotation(imageId);
        if (exifRot != GdPictureRotateFlipType.GdPictureRotateNoneFlipNone)
            img.ApplyExifRotation(imageId);

        // Aadhaar QR codes are dense and need ~350px+ on a side to decode.
        // Upscale the whole card so the long edge reaches a comfortable size,
        // and escalate / deskew if the first scan misses. (imageId is modified
        // in place, so the page appended to the output and the rect coordinates
        // stay in the same coordinate space.)
        bool ScanQr()
        {
            var st = img.BarcodeQRReaderDoScan(imageId, BarcodeQRReaderScanMode.BestQuality);
            return st == GdPictureStatus.OK && img.BarcodeQRReaderGetBarcodeCount() >= 1;
        }
        void EnsureLongEdge(int target)
        {
            int w = img.GetWidth(imageId), h = img.GetHeight(imageId);
            int le = Math.Max(w, h);
            if (le < target)
            {
                float s = (float)target / le;
                _logger.LogInformation("[page {P}] Aadhaar upscaling {W}x{H} → long edge {T}", pageNum, w, h, target);
                img.Resize(imageId, (int)(w * s), (int)(h * s),
                    GdPicture14.Imaging.GdPictureInterpolationMode.HighQualityBicubic);
            }
        }

        EnsureLongEdge(3600);
        bool found = ScanQr();
        if (!found) { img.BarcodeQRReaderClear(); img.AutoDeskew(imageId); found = ScanQr(); }
        if (!found) { img.BarcodeQRReaderClear(); EnsureLongEdge(5000); found = ScanQr(); }
        if (!found)
        {
            img.BarcodeQRReaderClear();
            _logger.LogInformation("[page {P}] No QR code found — not recognized as Aadhaar", pageNum);
            return (false, null, rectsOut, null);
        }

        // First QR; bounding box from its 4 corners.
        const int n = 1;
        int x1 = img.BarcodeQRReaderGetBarcodeX1(n), y1 = img.BarcodeQRReaderGetBarcodeY1(n);
        int x2 = img.BarcodeQRReaderGetBarcodeX2(n), y2 = img.BarcodeQRReaderGetBarcodeY2(n);
        int x3 = img.BarcodeQRReaderGetBarcodeX3(n), y3 = img.BarcodeQRReaderGetBarcodeY3(n);
        int x4 = img.BarcodeQRReaderGetBarcodeX4(n), y4 = img.BarcodeQRReaderGetBarcodeY4(n);

        // Capture & parse the QR payload (Aadhaar XML or Secure-QR) for the side panel.
        string qrValue = img.BarcodeQRReaderGetBarcodeValue(n) ?? "";
        var qrData = ParseAadhaarQr(qrValue);
        if (qrData != null)
            _logger.LogInformation("[page {P}] Parsed {Count} QR field(s)", pageNum, qrData.Count);

        img.BarcodeQRReaderClear();

        int qLeft = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
        int qRight = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
        int qTop = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
        int qBottom = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
        float qW = qRight - qLeft, qH = qBottom - qTop;
        if (qW <= 0 || qH <= 0) return (false, null, rectsOut, qrData);
        float q = (qW + qH) / 2f; // nominal QR side length — the layout unit

        _logger.LogInformation("[page {P}] Aadhaar QR at ({L},{T})-({R},{B}), side≈{Q}",
            pageNum, qLeft, qTop, qRight, qBottom, q);

        // --- OCR (used to pinpoint the 12-digit number) ---
        var lines = RunFullOcr(img, imageId);

        int blackColor = img.ARGBI(255, 0, 0, 0);
        foreach (var field in fieldsToRedact)
        {
            var rect = ResolveAadhaarFieldRect(field, lines, qLeft, qTop, qRight, qBottom, q);
            if (rect == null) continue;
            var (fx, fy, fw, fh) = rect.Value;
            rectsOut.Add((field, fx, fy, fw, fh));
            if (drawRedactions)
                img.DrawFilledRectangle(imageId, Math.Max(0, fx), Math.Max(0, fy), fw, fh, blackColor, false);
        }

        return (true, null, rectsOut, qrData);
    }

    /// <summary>
    /// Parses an Aadhaar QR payload into labelled key/value fields.
    /// Handles the legacy "PrintLetterBarcodeData" XML format and makes a
    /// best-effort attempt at the newer compressed "Secure QR" (numeric) format.
    /// Returns null when there is no recognisable structured data.
    /// </summary>
    private static List<QrField>? ParseAadhaarQr(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.TrimStart();

        // --- Legacy XML format ---
        if (trimmed.StartsWith("<"))
        {
            try
            {
                var root = System.Xml.Linq.XDocument.Parse(value).Root;
                if (root != null)
                {
                    var fields = new List<QrField>();
                    void Add(string attr, string label)
                    {
                        var a = root.Attribute(attr);
                        if (a != null && !string.IsNullOrWhiteSpace(a.Value))
                            fields.Add(new QrField { Label = label, Value = a.Value.Trim() });
                    }
                    Add("uid", "Aadhaar Number");
                    Add("name", "Name");
                    Add("gender", "Gender");
                    Add("dob", "Date of Birth");
                    Add("yob", "Year of Birth");
                    Add("co", "Care Of");
                    Add("house", "House");
                    Add("street", "Street");
                    Add("lm", "Landmark");
                    Add("loc", "Location");
                    Add("vtc", "Village / Town / City");
                    Add("po", "Post Office");
                    Add("subdist", "Sub District");
                    Add("dist", "District");
                    Add("state", "State");
                    Add("pc", "Pincode");
                    if (fields.Count > 0) return fields;
                }
            }
            catch { /* not valid XML — fall through */ }
        }

        // --- Newer compressed "Secure QR" (all-numeric) — best effort ---
        if (value.Length > 50 && value.All(char.IsDigit))
        {
            var fields = TryParseSecureQr(value);
            if (fields != null && fields.Count > 0) return fields;
            return new List<QrField>
            {
                new QrField { Label = "Format", Value = "Aadhaar Secure QR (compressed)" },
                new QrField { Label = "Payload size", Value = $"{value.Length} digits" },
            };
        }

        // --- Unknown QR — surface the raw text ---
        return new List<QrField>
        {
            new QrField { Label = "QR Data", Value = value.Length > 400 ? value[..400] + "…" : value }
        };
    }

    /// <summary>Best-effort decode of an Aadhaar Secure-QR numeric payload:
    /// BigInteger → bytes → GZip-inflate → fields delimited by 0xFF.</summary>
    private static List<QrField>? TryParseSecureQr(string numeric)
    {
        try
        {
            var bytes = System.Numerics.BigInteger.Parse(numeric).ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] data;
            using (var inMs = new MemoryStream(bytes))
            using (var gz = new System.IO.Compression.GZipStream(inMs, System.IO.Compression.CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                gz.CopyTo(outMs);
                data = outMs.ToArray();
            }

            // Split on 0xFF into the leading text segments (before the photo/signature).
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i < data.Length && parts.Count < 16; i++)
            {
                if (data[i] == 0xFF)
                {
                    parts.Add(System.Text.Encoding.UTF8.GetString(data, start, i - start));
                    start = i + 1;
                }
            }
            string Get(int i) => i < parts.Count ? parts[i].Trim() : "";

            var f = new List<QrField>();
            void A(string label, string v) { if (!string.IsNullOrWhiteSpace(v)) f.Add(new QrField { Label = label, Value = v }); }
            // V2 layout: [1]=referenceId [2]=name [3]=dob [4]=gender [5]=careOf
            //            [6]=district [7]=landmark [8]=house [9]=location
            //            [10]=pincode [11]=postOffice [12]=state [13]=street
            //            [14]=subDistrict [15]=vtc
            A("Reference ID", Get(1));
            A("Name", Get(2));
            A("Date of Birth", Get(3));
            A("Gender", Get(4));
            A("Care Of", Get(5));
            A("District", Get(6));
            A("Landmark", Get(7));
            A("House", Get(8));
            A("Location", Get(9));
            A("Pincode", Get(10));
            A("Post Office", Get(11));
            A("State", Get(12));
            A("Street", Get(13));
            A("Sub District", Get(14));
            A("VTC", Get(15));
            return f;
        }
        catch { return null; }
    }

    /// <summary>
    /// Maps an Aadhaar field to a redaction rectangle, expressed as vectors
    /// (offsets in QR-side-length units `q`) from the QR bounding box.
    /// The data column sits to the LEFT of the QR; the photo is further left.
    /// </summary>
    private (float X, float Y, float W, float H)? ResolveAadhaarFieldRect(
        string field, List<OcrLine> lines,
        int qLeft, int qTop, int qRight, int qBottom, float q)
    {
        // Offsets below are in QR-side-length units `q`, measured from the QR's
        // top-left corner. They follow the standard front-side layout, where the
        // photo sits left, the Name/DOB/Gender column in the middle, the QR on
        // the right, and the 12-digit number along the bottom.
        switch (field.ToLowerInvariant())
        {
            case "photo": // left column — fully covers the photograph
                return (qLeft - q * 3.95f, qTop - q * 0.95f, q * 1.62f, q * 1.95f);

            case "givennames": // Name (covers both the local-script and Latin lines)
                return (qLeft - q * 2.35f, qTop - q * 0.80f, q * 1.50f, q * 0.50f);

            case "dateofbirth": // whole DOB line incl. the local label and date value
                return (qLeft - q * 2.35f, qTop - q * 0.37f, q * 2.75f, q * 0.30f);

            case "sex": // Gender value (local script + MALE / FEMALE)
                return (qLeft - q * 2.35f, qTop - q * 0.10f, q * 1.40f, q * 0.32f);

            case "documentnumber": // 12-digit Aadhaar number
            {
                // Prefer the OCR'd 12-digit line — unambiguous and exact.
                var num = FindAadhaarNumberLine(lines);
                if (num != null)
                {
                    float pad = q * 0.10f;
                    return (num.Left - pad, num.Top - pad, num.Width + 2 * pad, num.Height + 2 * pad);
                }
                // Fallback: QR-relative box along the bottom of the data column.
                return (qLeft - q * 2.40f, qTop + q * 0.50f, q * 2.10f, q * 0.34f);
            }

            default:
                return null; // field not applicable to Aadhaar
        }
    }

    /// <summary>Find the OCR line that is exactly a 12-digit Aadhaar number
    /// (digits may be separated by spaces, e.g. "8416 1590 3267").</summary>
    private static OcrLine? FindAadhaarNumberLine(List<OcrLine> lines)
    {
        foreach (var l in lines.OrderBy(l => l.Top))
        {
            int digits = l.Text.Count(char.IsDigit);
            if (digits == 12 && l.Text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c)))
                return l;
        }
        return null;
    }

    /// <summary>Run a full English OCR pass and return the detected text lines.</summary>
    private List<OcrLine> RunFullOcr(GdPictureImaging img, int imageId)
    {
        var lines = new List<OcrLine>();
        using var ocr = new GdPictureOCR();
        ocr.ResourceFolder = _resourceFolder;
        try
        {
            ocr.ResetROI();
            ocr.SetImage(imageId);
            ocr.AddLanguage(OCRLanguage.English);
            string id = ocr.RunOCR();
            if (ocr.GetStat() == GdPictureStatus.OK)
            {
                int c = ocr.GetTextLineCount(id);
                for (int i = 0; i < c; i++)
                    lines.Add(new OcrLine
                    {
                        Text = ocr.GetTextLineValue(id, i) ?? "",
                        Left = ocr.GetTextLineLeft(id, i),
                        Top = ocr.GetTextLineTop(id, i),
                        Right = ocr.GetTextLineRight(id, i),
                        Bottom = ocr.GetTextLineBottom(id, i),
                    });
            }
        }
        finally { ocr.ReleaseOCRResults(); }
        return lines;
    }

    /// <summary>
    /// Stricter MRZ validation: detects false positives where the MRZ engine
    /// returns text from non-passport pages (forms, letters, etc.). Real TD3
    /// passport MRZ has 2 lines of 44 chars, line 1 starts with 'P' for
    /// passport, and contains only A–Z, 0–9, '&lt;', and spaces (with margin
    /// for OCR noise).
    /// </summary>
    private static bool LooksLikeRealMrz(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length >= 20)
            .ToList();
        if (lines.Count < 2) return false;

        // Take the 2 longest lines (the actual MRZ)
        var top2 = lines.OrderByDescending(l => l.Length).Take(2).ToList();
        // First MRZ line must start with 'P' (TD3 passport)
        if (!top2.Any(l => l.StartsWith("P"))) return false;
        // Must contain '<' filler — real MRZ has many; require at least 2
        // to avoid matching ordinary uppercase text that happens to have one '<'.
        if (!top2.Any(l => l.Count(c => c == '<') >= 2)) return false;

        // High proportion of MRZ-allowed characters
        foreach (var line in top2)
        {
            int allowed = line.Count(c => char.IsLetterOrDigit(c) || c == '<' || c == ' ');
            if ((double)allowed / line.Length < 0.85) return false;
        }
        return true;
    }

    /// <summary>Rotate the image back by `appliedDegrees` (clockwise) in a SINGLE step.
    /// Doing it as multiple 90° rotations causes cumulative padding/coordinate issues.</summary>
    private static void RotateBack(GdPictureImaging gdpictureImaging, int imageId, int appliedDegrees)
    {
        int undoDegrees = (360 - appliedDegrees) % 360;
        var rotation = undoDegrees switch
        {
            90  => GdPictureRotateFlipType.GdPictureRotate90FlipNone,
            180 => GdPictureRotateFlipType.GdPictureRotate180FlipNone,
            270 => GdPictureRotateFlipType.GdPictureRotate270FlipNone,
            _   => GdPictureRotateFlipType.GdPictureRotateNoneFlipNone,
        };
        if (rotation != GdPictureRotateFlipType.GdPictureRotateNoneFlipNone)
            gdpictureImaging.Rotate(imageId, rotation);
    }

    /// <summary>Append a (possibly redacted) image as a new page in the output PDF.</summary>
    private static void AppendImageAsPage(GdPicturePDF outputPdf, GdPictureImaging gdpictureImaging, int imageId)
    {
        int w = gdpictureImaging.GetWidth(imageId);
        int h = gdpictureImaging.GetHeight(imageId);
        float hRes = gdpictureImaging.GetHorizontalResolution(imageId);
        float vRes = gdpictureImaging.GetVerticalResolution(imageId);
        if (hRes <= 0) hRes = 72;
        if (vRes <= 0) vRes = 72;

        float pageW = w / hRes * 72f;
        float pageH = h / vRes * 72f;

        outputPdf.NewPage(pageW, pageH);
        string imgName = outputPdf.AddImageFromGdPictureImage(imageId, false, false);
        outputPdf.DrawImage(imgName, 0, 0, pageW, pageH);
    }

    /// <summary>
    /// Returns the pixel rectangle for a given passport field.
    /// All positions are defined as offsets in MRZ character-widths (cw)
    /// from the MRZ bounding box. This scales naturally with image DPI.
    ///
    /// Coordinate system:
    ///   X = mrzLeft + (xOffset * cw)         → rightward from MRZ left edge
    ///   Y = mrzTop  - (yAboveMrz * cw)       → upward from MRZ top edge
    ///
    /// Typical TD3 passport data page layout (ICAO 9303, 44 chars wide):
    /// ┌────────────────────────────────────────────────┐  ↑ ~25 cw above MRZ
    /// │  Type: P | Country: UTO | Passport No: XXXXXXX │
    /// │  Surname: XXXXXXXXXXXXXXXXXXXX                  │
    /// │  Given Name(s): XXXXXXXXXXXX                    │
    /// │  ┌──────────┐  Date of Birth: XX/XX/XXXX       │
    /// │  │  PHOTO   │  Place of Birth: XXXXXXXX        │
    /// │  │  (10cw)  │  Sex: X  Nationality: XXX        │
    /// │  └──────────┘                                   │
    /// │  Signature      Date of Issue | Date of Expiry  │
    /// │                                                  │  ↓ MRZ top
    /// │  P&lt;UTOSURNAME&lt;&lt;GIVENNAMES&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt; │
    /// │  Z9999999&lt;0UTO8505241M23000002&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;0 │
    /// └────────────────────────────────────────────────┘
    /// </summary>
    private class OcrLine
    {
        public string Text { get; set; } = "";
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int CenterY => (Top + Bottom) / 2;
    }

    // =====================================================================
    // Per-document-type redaction calculus
    // =====================================================================
    // Each ICAO document size lays its fields out differently, so the
    // geometry that maps a field name → redaction rectangle differs per type:
    //   TD3 (Passports)       : 2-line MRZ (44 chars), photo in left column
    //   TD1 (Card ID-1)       : 3-line MRZ (30 chars), ID-1 card, landscape
    //   TD2 (Card/Doc ID-2)   : 2-line MRZ (36 chars), ID-2 card
    // This dispatcher is the single branch point. TD3 is fully implemented;
    // TD1/TD2 currently reuse the TD3 passport calculus as a placeholder —
    // replace each stub with that type's real geometry once you have sample
    // documents in input-docs/td1 and /td2.
    private (float X, float Y, float W, float H)? ResolveFieldRectForDocType(
        string docType, string field, List<OcrLine> lines, MrzInfo mrz,
        int mrzLeft, int mrzTop, int mrzRight, int mrzBottom, float cw,
        int dataColumnLeft)
    {
        switch ((docType ?? "td3").ToLowerInvariant())
        {
            case "td1":
                // TODO: implement TD1 (ID-1 card, 3-line MRZ) geometry.
                _logger.LogInformation("[td1] calculus not yet defined — using passport (TD3) geometry for '{Field}'", field);
                return ResolveFieldRect(field, lines, mrz, mrzLeft, mrzTop, mrzRight, mrzBottom, cw, dataColumnLeft);

            case "td2":
                // TODO: implement TD2 (ID-2 card, 2-line 36-char MRZ) geometry.
                _logger.LogInformation("[td2] calculus not yet defined — using passport (TD3) geometry for '{Field}'", field);
                return ResolveFieldRect(field, lines, mrz, mrzLeft, mrzTop, mrzRight, mrzBottom, cw, dataColumnLeft);

            // NOTE: "aadhaar" never reaches here — it is handled by its own
            // QR-anchored pipeline (ProcessAadhaarImage), branched in ProcessSingleImage.

            case "td3":
            default:
                // TD3 = passport. Fully-implemented label/MRZ-anchored calculus.
                return ResolveFieldRect(field, lines, mrz, mrzLeft, mrzTop, mrzRight, mrzBottom, cw, dataColumnLeft);
        }
    }

    /// <summary>
    /// Locates the redaction rectangle for a field by:
    ///   1. Searching OCR lines for label keywords (e.g. "Given Name").
    ///   2. Finding the value line — typically the next line BELOW the label
    ///      whose horizontal range overlaps the label.
    ///   3. Returning the value line's bbox (with padding) as the redaction zone.
    /// Falls back to MRZ-anchored geometry if the label is not found.
    /// </summary>
    private (float X, float Y, float W, float H)? ResolveFieldRect(
        string field, List<OcrLine> lines, MrzInfo mrz,
        int mrzLeft, int mrzTop, int mrzRight, int mrzBottom, float cw,
        int dataColumnLeft)
    {
        var f = field.ToLowerInvariant();

        // --- Special case: MRZ — we already know the box ---
        if (f == "mrz")
        {
            float pad = cw * 0.4f;
            return (mrzLeft - pad, mrzTop - pad,
                    (mrzRight - mrzLeft) + 2 * pad,
                    (mrzBottom - mrzTop) + 2 * pad);
        }

        // --- Special case: Photo — extends from left edge to the data column ---
        // On a TD3 data page the photo occupies the entire LEFT column.
        if (f == "photo")
        {
            float x = Math.Max(0, mrzLeft - cw * 3f);     // start near the document's left
            float w = (dataColumnLeft - x) - cw * 0.5f;   // up to the data column (small gap)
            float y = mrzTop - cw * 23.0f;                // higher: closer to the header
            float h = cw * 15f;                           // taller box to fully cover photo
            return (x, y, w, h);
        }

        // --- Surname & Given Names: find the OCR line whose text matches the
        //     MRZ-parsed name (fuzzy). MRZ is reliable; visible labels often aren't. ---
        if ((f == "surname" || f == "givennames") && mrz != null)
        {
            string target = f == "surname" ? mrz.Surname : mrz.GivenNames;
            if (!string.IsNullOrWhiteSpace(target))
            {
                var match = FindLineMatchingName(lines, target, mrzTop, dataColumnLeft, mrzLeft);
                if (match != null)
                {
                    float pad = cw * 0.4f;
                    // Cover from data-column left to MRZ right (catches name parts the
                    // OCR may have missed and is still safely inside the data zone).
                    float xL = dataColumnLeft - pad;
                    float xR = mrzRight - cw * 1f;
                    return (xL, match.Top - pad, xR - xL, match.Height + 2 * pad);
                }
            }
        }

        // --- Label keywords for each field (case-insensitive substring match) ---
        // Includes common OCR misreadings.
        var keywords = f switch
        {
            "surname"        => new[] { "surname", "sumame", "sumarne" },
            "givennames"     => new[] { "given name", "given names", "given nam" },
            "dateofbirth"    => new[] { "date of birth", "dateof birth", "date ofbirth" },
            "placeofbirth"   => new[] { "place of birth" },
            "sex"            => new[] { "sex" },
            "nationality"    => new[] { "nationality" },
            "documentnumber" => new[] { "passport no", "passport n", "document no" },
            "countrycode"    => new[] { "country code", "country" },
            "placeofissue"   => new[] { "place of issue", "of issue", "issue" },
            "dateofissue"    => new[] { "date of issue", "of issue", "issue" },
            "expirydate"     => new[] { "date of expiry", "expiry", "epiry", "expir", "expi", "epir" },
            "signature"      => new[] { "signature", "holder" },
            _ => Array.Empty<string>()
        };
        if (keywords.Length == 0) return null;

        var label = FindLabel(lines, keywords, mrzTop);
        if (label == null)
        {
            _logger.LogInformation("Label not found for {Field} — using MRZ-relative fallback", f);
            return FallbackRect(f, mrzLeft, mrzTop, mrzRight, cw);
        }

        // Validate label position is in a plausible region (above MRZ, within data zone width).
        // Reject labels that are too close to the MRZ (likely OCR noise on the MRZ itself)
        // or far outside the MRZ horizontal range.
        float dataZoneTop = mrzTop - cw * 27f;  // approx top of data zone
        float dataZoneLeft = mrzLeft - cw * 1.5f;
        float dataZoneRight = mrzRight + cw * 1.5f;
        if (label.Top > mrzTop - cw * 2f || label.Bottom < dataZoneTop ||
            label.Left < dataZoneLeft || label.Right > dataZoneRight)
        {
            _logger.LogInformation("Label for {Field} outside expected zone — using MRZ-relative fallback", f);
            return FallbackRect(f, mrzLeft, mrzTop, mrzRight, cw);
        }

        _logger.LogInformation("Found label for {Field}: '{Text}' at ({L},{T})-({R},{B})",
            f, label.Text, label.Left, label.Top, label.Right, label.Bottom);

        // --- Short fields printed with their label above the value (most TD3 data pages):
        //     when the label line holds no value, take the value line below it. ---
        bool shortField = f == "sex" || f == "nationality" || f == "countrycode" || f == "documentnumber";
        if (shortField && !HasInlineValue(label.Text, keywords))
        {
            var below = FindLineBelow(lines, label, mrzTop);
            if (below != null && below.Top > label.Bottom
                && below.Top - label.Bottom <= label.Height * 4.5f && LooksLikeValue(below.Text))
            {
                float pad = cw * 0.3f;
                return (below.Left - pad, below.Top - pad, below.Width + 2 * pad, below.Height + 2 * pad);
            }
        }

        // --- For "Sex", "Nationality" and "Country Code" — value is on SAME line, to the right ---
        if (f == "sex" || f == "nationality" || f == "countrycode")
        {
            float x = label.Right + cw * 0.3f;
            float y = label.Top - cw * 0.2f;
            float w = cw * 6.0f;
            float h = label.Height + cw * 0.4f;
            return (x, y, w, h);
        }

        // --- For "Passport No." — value is on SAME line, to the right ---
        if (f == "documentnumber")
        {
            float x = label.Right + cw * 0.3f;
            float y = label.Top - cw * 0.2f;
            float w = cw * 11.0f;
            float h = label.Height + cw * 0.4f;
            return (x, y, w, h);
        }

        // --- For most fields: value is on the LINE BELOW the label ---
        var valueLine = FindLineBelow(lines, label, mrzTop);
        if (valueLine != null)
        {
            // Sanity-check the value line: distance from label, size
            float verticalGap = valueLine.Top - label.Bottom;
            float maxGap = label.Height * 4.5f;
            bool plausible = verticalGap > 0 && verticalGap <= maxGap
                && valueLine.Width >= label.Height
                && LooksLikeValue(valueLine.Text);

            if (plausible)
            {
                float pad = cw * 0.3f;
                return (valueLine.Left - pad, valueLine.Top - pad,
                        valueLine.Width + 2 * pad,
                        valueLine.Height + 2 * pad);
            }
            _logger.LogInformation("Value line for {Field} looks implausible (gap={Gap}, text={Text}) — fallback",
                f, verticalGap, valueLine.Text);
        }

        // Couldn't confidently locate value — fall back to MRZ-relative geometry
        return FallbackRect(f, mrzLeft, mrzTop, mrzRight, cw);
    }

    /// <summary>Detect the X coordinate where the data-field column starts.
    /// Most passport labels and values share a common left X. We pick the modal
    /// (most frequent) `Left` from value-looking lines, ignoring outliers.</summary>
    private static int? DetectDataColumnLeft(List<OcrLine> lines, int mrzLeft, int mrzRight, int mrzTop)
    {
        var candidates = lines
            .Where(l => l.Top < mrzTop - 10 && l.Top > mrzTop / 6
                && l.Left > mrzLeft && l.Right < mrzRight + 5
                && l.Text.Trim().Length >= 3)
            .Select(l => l.Left)
            .ToList();
        if (candidates.Count < 3) return null;

        // Cluster left positions: group within 25px of each other.
        var groups = candidates
            .GroupBy(x => x / 25)
            .OrderByDescending(g => g.Count())
            .ToList();
        return (int)groups.First().Average();
    }

    /// <summary>Find an OCR line whose text matches `target` (a name from the MRZ),
    /// allowing for OCR errors. Score is LCS normalized by MAX length to penalize
    /// long unrelated lines that happen to share many characters.</summary>
    private static OcrLine? FindLineMatchingName(
        List<OcrLine> lines, string target, int mrzTop, int dataColumnLeft, int mrzLeft)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var clean = new string(target.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        if (clean.Length < 3) return null;

        OcrLine? best = null;
        double bestScore = 0.5; // minimum score to accept

        foreach (var l in lines)
        {
            if (l.Top >= mrzTop) continue;
            var lineText = new string(l.Text.Where(char.IsLetter).ToArray()).ToUpperInvariant();
            if (lineText.Length < 2) continue;

            // Length-normalized LCS — penalizes long unrelated lines.
            int lcs = LongestCommonSubsequence(clean, lineText);
            double score = (double)lcs / Math.Max(clean.Length, lineText.Length);

            // Boost: line is mostly uppercase letters (passport values are uppercase)
            int upper = l.Text.Count(char.IsUpper);
            int lower = l.Text.Count(char.IsLower);
            if (upper >= 3 && upper > lower * 2) score += 0.15;

            // Boost: line is in the data column (where values are)
            int colCenter = (dataColumnLeft + mrzLeft) / 2;
            if (l.Left >= colCenter - 30) score += 0.05;

            // Penalty: line is much longer than the target — likely a label or sentence
            if (lineText.Length > clean.Length * 2.5) score -= 0.2;

            if (score > bestScore)
            {
                bestScore = score;
                best = l;
            }
        }
        return best;
    }

    private static int LongestCommonSubsequence(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int[,] dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Length, b.Length];
    }

    /// <summary>True when the label line also carries a value after the keyword
    /// ("Sex: F"), false when it is the label alone ("Passport No.").</summary>
    private static bool HasInlineValue(string text, string[] keywords)
    {
        var lower = text.ToLowerInvariant();
        foreach (var k in keywords.OrderByDescending(k => k.Length))
        {
            int i = lower.IndexOf(k, StringComparison.Ordinal);
            if (i < 0) continue;
            var after = text[(i + k.Length)..];
            return after.Any(char.IsDigit)
                || after.Split(new[] { ' ', ':', '/', '.' }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(w => w.All(char.IsUpper));
        }
        return false;
    }

    /// <summary>Find the OCR line whose text contains any of the given keywords, above the MRZ.</summary>
    private static OcrLine? FindLabel(List<OcrLine> lines, string[] keywords, int mrzTop)
    {
        // Keywords go from the most specific to the loosest ("date of issue", then "issue"):
        // the first keyword found wins, so "Place of Issue" is not taken for "Date of Issue".
        foreach (var k in keywords)
        {
            OcrLine? best = null;
            foreach (var l in lines)
            {
                if (l.Top >= mrzTop) continue; // only labels above MRZ
                if (!l.Text.ToLowerInvariant().Contains(k)) continue;
                // Prefer the topmost match (passport labels appear once)
                if (best == null || l.Top < best.Top) best = l;
            }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>Find the next OCR line below `label` that horizontally overlaps with it
    /// and looks like a VALUE (uppercase text, dates, etc.) rather than another label.</summary>
    private static OcrLine? FindLineBelow(List<OcrLine> lines, OcrLine label, int mrzTop)
    {
        // Sort all candidate lines below the label by vertical distance
        var candidates = lines
            .Where(l => l != label
                && l.Top > label.Bottom
                && l.Top < mrzTop
                && l.Left <= label.Right + 30
                && l.Right >= label.Left - 30)
            .OrderBy(l => l.Top)
            .ToList();

        // First, prefer a line that looks like a VALUE
        foreach (var l in candidates)
        {
            if (LooksLikeValue(l.Text)) return l;
        }
        // Fallback: the closest line below regardless of content
        return candidates.FirstOrDefault();
    }

    /// <summary>A "value" line is uppercase text, a date, a number — not another label.
    /// Labels typically contain words like "Name", "Birth", "of", lowercase letters, etc.</summary>
    private static bool LooksLikeValue(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        // Avoid matching common label words
        var lc = t.ToLowerInvariant();
        var labelWords = new[] {
            "name", "surname", "birth", "issue", "expiry", "expire",
            "nationality", "place", "date", "type", "code", "passport",
            "country", "given", "father", "mother", "spouse", "address",
            "holder", "signature", "sex", "no.", "number"
        };
        if (labelWords.Any(w => lc.Contains(w))) return false;
        // Looks like a value if it's mostly uppercase letters / digits / dates
        int strong = t.Count(c => char.IsUpper(c) || char.IsDigit(c) || c == '/' || c == '-');
        return (double)strong / t.Length >= 0.5;
    }

    /// <summary>MRZ-anchored fallback rectangle when a label cannot be found.
    /// Typical TD3 data page: data zone ≈ 0.50 × mrzWidth tall,
    /// MRZ char-width as the unit. Values are y-offsets ABOVE mrzTop in cw units.</summary>
    private static (float X, float Y, float W, float H) FallbackRect(
        string f, int mrzLeft, int mrzTop, int mrzRight, float cw)
    {
        return f switch
        {
            "documentnumber" => (mrzLeft + cw * 35f, mrzTop - cw * 22.5f, cw * 11f, cw * 2.2f),
            "countrycode"    => (mrzLeft + cw * 26f, mrzTop - cw * 22.5f, cw *  6f, cw * 2.2f),
            "surname"        => (mrzLeft + cw * 11f, mrzTop - cw * 17.0f, cw * 24f, cw * 2.2f),
            "givennames"     => (mrzLeft + cw * 11f, mrzTop - cw * 14.0f, cw * 24f, cw * 2.2f),
            "nationality"    => (mrzLeft + cw * 17f, mrzTop - cw * 13.5f, cw * 10f, cw * 2.0f),
            "sex"            => (mrzLeft + cw * 28f, mrzTop - cw * 13.5f,  cw * 4f, cw * 2.0f),
            "dateofbirth"    => (mrzLeft + cw * 35f, mrzTop - cw * 13.5f, cw * 11f, cw * 2.0f),
            "placeofbirth"   => (mrzLeft + cw * 15f, mrzTop - cw * 10.5f, cw * 24f, cw * 2.0f),
            "placeofissue"   => (mrzLeft + cw * 15f, mrzTop - cw *  7.5f, cw * 18f, cw * 2.0f),
            "dateofissue"    => (mrzLeft + cw * 19f, mrzTop - cw *  4.5f, cw * 11f, cw * 2.2f),
            "expirydate"     => (mrzLeft + cw * 35f, mrzTop - cw *  4.5f, cw * 10f, cw * 2.0f),
            "signature"      => (mrzLeft + cw * 2f, mrzTop - cw * 7.0f, cw * 16f, cw * 5.0f),
            _ => (0, 0, 0, 0)
        };
    }

    private static MrzInfo ParseMrz(string[] lines)
    {
        string line1 = lines[0].PadRight(44);
        string line2 = lines.Length > 1 ? lines[1].PadRight(44) : new string('<', 44);

        // Line 1: P<UTOSURNAME<<GIVENNAMES<<<<<<<<<<<<<<<<<<
        string namesRaw = line1.Substring(5, Math.Min(39, line1.Length - 5)).TrimEnd('<');
        var nameParts = namesRaw.Split("<<", 2);
        string surname = nameParts[0].Replace('<', ' ').Trim();
        string givenNames = nameParts.Length > 1 ? nameParts[1].Replace('<', ' ').Trim() : "";
        // A run of fillers ends the names; OCR noise after it (a filler read as "2") is dropped.
        givenNames = System.Text.RegularExpressions.Regex.Replace(givenNames, @"\s{2,}.*$", "");

        // Line 2: L898902C36UTO7408122F1204159ZE184226B<<<<1
        return new MrzInfo
        {
            IssuingState = line1.Substring(2, 3).Replace("<", "").Trim(),
            Surname = surname,
            GivenNames = givenNames,
            DocumentNumber = line2.Substring(0, 9).Replace("<", "").Trim(),
            Nationality = line2.Substring(10, 3).Replace("<", "").Trim(),
            DateOfBirth = FormatMrzDate(line2.Substring(13, 6)),
            Sex = line2.Substring(20, 1).Replace("<", ""),
            ExpiryDate = FormatMrzDate(line2.Substring(21, 6)),
        };
    }

    /// <summary>Converts YYMMDD to a readable date string.</summary>
    private static string FormatMrzDate(string yymmdd)
    {
        if (yymmdd.Length < 6) return yymmdd;
        return $"{yymmdd[..2]}/{yymmdd[2..4]}/{yymmdd[4..6]}";
    }

    public void Dispose() { /* GdPicture objects are disposed per-call */ }
}
