using GdPicture14;
using Microsoft.Extensions.FileProviders;
using PassportRedaction.Models;
using PassportRedaction.Services;

// --- GdPicture license: lazy init to avoid native crash on startup ---
var gdpInitialized = false;
var gdpLock = new object();
void EnsureGdPictureInit()
{
    if (gdpInitialized) return;
    lock (gdpLock)
    {
        if (gdpInitialized) return;
        var lm = new LicenseManager();
        // Empty string activates GdPicture trial mode.
        var key = Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "";
        lm.RegisterKEY(key);
        gdpInitialized = true;
        Console.WriteLine("GdPicture initialized.");
    }
}

var builder = WebApplication.CreateBuilder(args);

// ASPNETCORE_URLS overrides the default address.
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is { Length: > 0 } urls
    ? urls
    : "http://localhost:5330");

builder.Services.AddSingleton<PassportRedactionService>();
builder.Services.AddHttpClient();

var app = builder.Build();

// --- Static files ---
app.UseStaticFiles(); // serves wwwroot

var inputDocsPath = Path.Combine(Directory.GetCurrentDirectory(), "input-docs");
var outputDocsPath = Path.Combine(Directory.GetCurrentDirectory(), "output-docs");
Directory.CreateDirectory(outputDocsPath);

// Document categories — each maps to a subfolder of input-docs and to a tab
// in the redaction modal. Keep this list in sync with the frontend CATEGORIES.
var docCategories = new[] { "td1", "td2", "td3", "aadhaar" };
foreach (var c in docCategories)
    Directory.CreateDirectory(Path.Combine(inputDocsPath, c));

// Resolve a category + filename to an absolute path inside that category's
// subfolder, guarding against path traversal. Returns null if invalid.
string? ResolveInputPath(string category, string fileName)
{
    var safeCategory = Path.GetFileName(category ?? "");
    if (!docCategories.Contains(safeCategory)) return null;
    var safeFileName = Path.GetFileName(fileName ?? "");
    if (string.IsNullOrWhiteSpace(safeFileName)) return null;
    return Path.Combine(inputDocsPath, safeCategory, safeFileName);
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(inputDocsPath),
    RequestPath = "/input-docs",
    ServeUnknownFileTypes = true
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(outputDocsPath),
    RequestPath = "/output-docs",
    ServeUnknownFileTypes = true
});

// --- API: List documents grouped by category ---
app.MapGet("/api/documents", () =>
{
    var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".pdf"
    };

    var grouped = docCategories.ToDictionary(
        category => category,
        category =>
        {
            var dir = Path.Combine(inputDocsPath, category);
            if (!Directory.Exists(dir)) return new List<object>();
            return Directory.GetFiles(dir)
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
                .Select(f => (object)new
                {
                    name = Path.GetFileName(f),
                    category,
                    url = $"/api/documents/pdf/{category}/{Uri.EscapeDataString(Path.GetFileName(f))}"
                })
                .OrderBy(f => ((dynamic)f).name)
                .ToList();
        });

    return Results.Ok(grouped);
});


// --- API: Serve any document as PDF (converts images on the fly) ---
app.MapGet("/api/documents/pdf/{category}/{filename}", (string category, string filename, ILogger<Program> logger) =>
{
    var filePath = ResolveInputPath(category, filename);
    if (filePath == null)
        return Results.BadRequest("Invalid document category or name.");

    var safeFileName = Path.GetFileName(filePath);

    if (!File.Exists(filePath))
        return Results.NotFound("Document not found.");

    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    if (ext == ".pdf")
        return Results.File(filePath, "application/pdf");

    // Convert image to PDF using GdPicture
    EnsureGdPictureInit();
    using var gdpictureImaging = new GdPictureImaging();
    int imageId;
    try
    {
        imageId = gdpictureImaging.CreateGdPictureImageFromFile(filePath);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "GdPicture CreateGdPictureImageFromFile failed for {File}", safeFileName);
        return Results.Problem($"GdPicture error: {ex.Message}");
    }
    if (imageId == 0)
        return Results.Problem($"Failed to load image: {gdpictureImaging.GetStat()}");

    try
    {
        using var gdpicturePdf = new GdPicturePDF();
        gdpicturePdf.NewPDF();

        int w = gdpictureImaging.GetWidth(imageId);
        int h = gdpictureImaging.GetHeight(imageId);
        float hRes = gdpictureImaging.GetHorizontalResolution(imageId);
        float vRes = gdpictureImaging.GetVerticalResolution(imageId);
        if (hRes <= 0) hRes = 72;
        if (vRes <= 0) vRes = 72;

        float pageW = w / hRes * 72f;
        float pageH = h / vRes * 72f;

        gdpicturePdf.NewPage(pageW, pageH);
        string imgName = gdpicturePdf.AddImageFromGdPictureImage(imageId, false, false);
        gdpicturePdf.DrawImage(imgName, 0, 0, pageW, pageH);

        using var ms = new MemoryStream();
        gdpicturePdf.SaveToStream(ms);
        gdpicturePdf.CloseDocument();

        logger.LogInformation("Converted {File} to PDF ({Bytes} bytes)", safeFileName, ms.Length);
        return Results.File(ms.ToArray(), "application/pdf", $"{Path.GetFileNameWithoutExtension(safeFileName)}.pdf");
    }
    finally
    {
        gdpictureImaging.ReleaseGdPictureImage(imageId);
    }
});

// --- API: Redact passport ---
// --- API: Preview redactions — returns coords + a processed PDF without redactions
//     drawn. The frontend places moveable RedactionAnnotation markers on the
//     viewer; the user adjusts them and applies via the SDK's applyRedactions(). ---
app.MapPost("/api/redaction/preview", (RedactionRequest request, PassportRedactionService service, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.FileName))
        return Results.BadRequest(new RedactionPreviewResponse { Success = false, Message = "No file name provided." });
    if (request.Fields == null || request.Fields.Count == 0)
        return Results.BadRequest(new RedactionPreviewResponse { Success = false, Message = "No fields selected." });

    var inputPath = ResolveInputPath(request.DocType, request.FileName);
    if (inputPath == null)
        return Results.BadRequest(new RedactionPreviewResponse { Success = false, Message = "Invalid document category or name." });
    var safeFileName = Path.GetFileName(inputPath);
    if (!File.Exists(inputPath))
        return Results.NotFound(new RedactionPreviewResponse { Success = false, Message = "Document not found." });

    EnsureGdPictureInit();

    var previewName = $"preview_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Path.ChangeExtension(safeFileName, ".pdf")}";
    var previewPath = Path.Combine(outputDocsPath, previewName);

    logger.LogInformation("Previewing redactions for {Input} ({DocType}) -> {Output}, fields: [{Fields}]",
        safeFileName, request.DocType, previewName, string.Join(", ", request.Fields));

    try
    {
        var result = service.PreviewRedaction(inputPath, previewPath, request.Fields, request.DocType);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        var msg = ex.Message.Contains("MRZReader")
            ? "GdPicture license is missing the MRZReader feature."
            : $"Preview failed: {ex.Message}";
        logger.LogError(ex, "Preview redaction threw exception");
        return Results.Ok(new RedactionPreviewResponse { Success = false, Message = msg });
    }
});

app.MapPost("/api/redaction/redact", (RedactionRequest request, PassportRedactionService service, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.FileName))
        return Results.BadRequest(new RedactionResponse { Success = false, Message = "No file name provided." });

    if (request.Fields == null || request.Fields.Count == 0)
        return Results.BadRequest(new RedactionResponse { Success = false, Message = "No fields selected for redaction." });

    // Resolve + sanitize path from the document's category subfolder
    var inputPath = ResolveInputPath(request.DocType, request.FileName);
    if (inputPath == null)
        return Results.BadRequest(new RedactionResponse { Success = false, Message = "Invalid document category or name." });
    var safeFileName = Path.GetFileName(inputPath);

    if (!File.Exists(inputPath))
        return Results.NotFound(new RedactionResponse { Success = false, Message = "Document not found." });

    EnsureGdPictureInit();

    var outputFileName = $"redacted_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{safeFileName}";
    // Always output as PDF
    outputFileName = Path.ChangeExtension(outputFileName, ".pdf");
    var outputPath = Path.Combine(outputDocsPath, outputFileName);

    logger.LogInformation("Redacting {Input} ({DocType}) -> {Output}, fields: [{Fields}]",
        safeFileName, request.DocType, outputFileName, string.Join(", ", request.Fields));

    RedactionResponse result;
    try
    {
        result = service.RedactPassport(inputPath, outputPath, request.Fields, request.DocType);
    }
    catch (Exception ex)
    {
        var msg = ex.Message.Contains("MRZReader")
            ? "GdPicture license is missing the MRZReader feature. Request an MRZ-enabled license from Nutrient."
            : $"Redaction failed: {ex.Message}";
        logger.LogError(ex, "Redaction threw exception");
        return Results.Ok(new RedactionResponse { Success = false, Message = msg });
    }

    if (!result.Success)
    {
        logger.LogWarning("Redaction failed: {Message}", result.Message);
        return Results.Ok(result);
    }

    result.RedactedFileName = outputFileName;
    return Results.Ok(result);
});

app.MapFallbackToFile("index.html");

app.Run();
