namespace B1SLayer;

/// <summary>
/// Identifies whether a batch payload log entry is for the outgoing request or the incoming response.
/// </summary>
public enum SLBatchPayloadKind
{
    /// <summary>The raw multipart body sent to the Service Layer.</summary>
    Request,
    /// <summary>The raw multipart body received from the Service Layer.</summary>
    Response
}