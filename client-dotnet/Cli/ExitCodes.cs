using System.Net;

namespace AuroraFileBucket.Client.Cli;

/// <summary>Process exit codes.</summary>
public static class ExitCodes
{
    /// <summary>Transfer finished and the receiver verified the file.</summary>
    public const int Success = 0;

    /// <summary>Unclassified failure.</summary>
    public const int Failure = 1;

    /// <summary>Invalid command line.</summary>
    public const int InvalidUsage = 2;

    /// <summary>Authentication or authorisation failure.</summary>
    public const int Authentication = 3;

    /// <summary>Protocol or data error reported by the receiver (400, 404, 409, 413).</summary>
    public const int Protocol = 4;

    /// <summary>Network or server failure that survived the retries.</summary>
    public const int Network = 5;

    /// <summary>Local file or resume database failure.</summary>
    public const int LocalState = 6;

    public static int ForStatus(HttpStatusCode statusCode) => (int)statusCode switch
    {
        401 or 403 => Authentication,
        400 or 404 or 409 or 413 => Protocol,
        429 or >= 500 => Network,
        _ => Failure,
    };
}

/// <summary>Raised for an invalid command line; the caller prints the help text.</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }
}
