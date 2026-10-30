namespace PassportRedaction.Models;

public class RedactionRequest
{
    public string FileName { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = new();

    /// <summary>Document category: "td1", "td2", or "td3" (passport).
    /// Determines which redaction calculus the service applies and which
    /// subfolder of input-docs the file is read from.</summary>
    public string DocType { get; set; } = "td3";
}

public class RedactionResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? RedactedFileName { get; set; }
    public MrzInfo? MrzData { get; set; }
}

public class RedactionPreviewResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? PreviewDocumentUrl { get; set; }
    public List<RedactionRect> Redactions { get; set; } = new();
    public MrzInfo? MrzData { get; set; }

    /// <summary>Human-readable QR format label (e.g. "Aadhaar QR (XML)").</summary>
    public string? QrFormat { get; set; }
    /// <summary>Parsed key/value data embedded in the document's QR code, if any.</summary>
    public List<QrField>? QrData { get; set; }
}

/// <summary>A single parsed field from a document's QR code.</summary>
public class QrField
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}


public class RedactionRect
{
    public string Field { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    /// <summary>X in PDF points (top-left origin).</summary>
    public float X { get; set; }
    /// <summary>Y in PDF points (top-left origin).</summary>
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public class MrzInfo
{
    public string? Surname { get; set; }
    public string? GivenNames { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Nationality { get; set; }
    public string? DateOfBirth { get; set; }
    public string? Sex { get; set; }
    public string? ExpiryDate { get; set; }
    public string? IssuingState { get; set; }
}
