using System.Net;

namespace Kura.Core.Net;

/// <summary>User-facing failure categories (successor of the original FailureType, SPEC 1.6).</summary>
public enum FailureKind
{
    Connection,
    Timeout,
    CannotParse,
    InvalidCertificate,
    InvalidCredentials,
    Forbidden,
    NotFound,
    Throttled,
    InvalidParameters,
    ServerError,
    ServiceUnavailable,
    Other,
}

public sealed class BooruRequestException : Exception
{
    public BooruRequestException(FailureKind kind, string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = status;
    }

    public FailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }

    public static FailureKind Classify(HttpStatusCode code) => (int)code switch
    {
        401 => FailureKind.InvalidCredentials,
        403 => FailureKind.Forbidden,
        404 => FailureKind.NotFound,
        420 or 421 or 429 => FailureKind.Throttled,
        422 or 424 or 400 => FailureKind.InvalidParameters,
        503 => FailureKind.ServiceUnavailable,
        >= 500 => FailureKind.ServerError,
        _ => FailureKind.Other,
    };

    public static string Describe(FailureKind kind) => kind switch
    {
        FailureKind.Connection => "Couldn't reach the server.",
        FailureKind.Timeout => "The server took too long to respond.",
        FailureKind.CannotParse => "The server's response couldn't be read. Its API may have changed.",
        FailureKind.InvalidCertificate => "The server's TLS certificate is invalid.",
        FailureKind.InvalidCredentials => "The server rejected your login or API key.",
        FailureKind.Forbidden => "Access denied (403). The site may need a login or block this client.",
        FailureKind.NotFound => "Not found (404). Check the server URL and engine type.",
        FailureKind.Throttled => "The server is rate limiting requests. Try again shortly.",
        FailureKind.InvalidParameters => "The server rejected the search.",
        FailureKind.ServerError => "The server had an internal error.",
        FailureKind.ServiceUnavailable => "The server is temporarily unavailable.",
        _ => "The request failed.",
    };
}
