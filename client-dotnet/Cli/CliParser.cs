using System.Globalization;

namespace AuroraFileBucket.Client.Cli;

/// <summary>
/// Parser of the command line. The flag names and their semantics are identical to the ones of the
/// Python client.
/// </summary>
public static class CliParser
{
    public const string HelpText = """
        Aurora File Bucket sender v0.1.0 (windows-dotnet)

        Usage:
          aurora-send --server <url> --file <path> [options]
          aurora-send status --server <url> --upload-id <id> [options]
          aurora-send --help

        Required:
          --server <url>            Base URL of the receiver, e.g. https://bucket.example.com
          --file <path>             Absolute path of the file to upload (up to 100 GB)

        Authentication (--token, or --email together with --password):
          --email <email>           Account e-mail used to log in
          --password <password>     Account password used to log in
          --token <jwt>             Reuse an existing access token instead of logging in

        Transfer options:
          --vault <slug>            Target vault slug; defaults to the first vault allowed by the token
          --chunk-size-mib <4-32>   Chunk size in MiB (default 16)
          --concurrency <1-6>       Parts uploaded in parallel (default 3)
          --max-bandwidth-mbps <1-1000>
                                   Global shared bandwidth cap in Mbps (default 50)
          --no-compress             Disable zstd compression for the whole session
          --max-retries <n>         Retry attempts per request after the first failure (default 5)
          --no-resume               Ignore the local state database and start a new session
          --state-db <path>         Local SQLite resume database (default aurora_state.db)
          --json-progress           Emit machine readable JSON progress lines on stdout
          --adaptive                Adapt the number of parallel parts to the observed latency
          --help                    Show this help text

        status sub command:
          --upload-id <id>          Identifier of the upload session to query

        Exit codes: 0 success, 1 failure, 2 invalid usage, 3 authentication, 4 protocol or data
        error, 5 network or server failure, 6 local file or state error.
        """;

    public static CliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var command = CommandKind.Upload;
        var commandSeen = false;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (commandSeen)
                {
                    throw new UsageException($"Unexpected argument '{argument}'.");
                }

                command = argument.ToLowerInvariant() switch
                {
                    "status" => CommandKind.Status,
                    "upload" or "send" => CommandKind.Upload,
                    _ => throw new UsageException($"Unknown command '{argument}'. Expected 'status' or none."),
                };

                commandSeen = true;
                continue;
            }

            string name = argument[2..];
            string? inlineValue = null;
            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inlineValue = name[(equals + 1)..];
                name = name[..equals];
            }

            name = name.ToLowerInvariant();
            switch (name)
            {
                case "no-compress":
                case "no-resume":
                case "json-progress":
                case "adaptive":
                case "help":
                case "h":
                    if (inlineValue is not null)
                    {
                        throw new UsageException($"Flag '--{name}' does not take a value.");
                    }

                    flags.Add(name);
                    continue;
            }

            string value = inlineValue ?? ReadValue(args, ref i, name);
            values[name] = value;
        }

        if (flags.Contains("help") || flags.Contains("h"))
        {
            return new CliOptions { Command = CommandKind.Help };
        }

        string? server = Get(values, "server");
        string? file = Get(values, "file");
        string? vault = Get(values, "vault");
        string? email = Get(values, "email");
        string? password = Get(values, "password");
        string? token = Get(values, "token");
        string? stateDb = Get(values, "state-db");
        string? uploadId = Get(values, "upload-id");

        int? chunkSize = GetInt(values, "chunk-size-mib");
        int? concurrency = GetInt(values, "concurrency");
        int? bandwidth = GetInt(values, "max-bandwidth-mbps");
        int? maxRetries = GetInt(values, "max-retries");

        if (command == CommandKind.Status)
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                throw new UsageException("The 'status' command requires --server.");
            }

            if (string.IsNullOrWhiteSpace(uploadId))
            {
                throw new UsageException("The 'status' command requires --upload-id.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                throw new UsageException("--server is required.");
            }

            if (string.IsNullOrWhiteSpace(file))
            {
                throw new UsageException("--file is required.");
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                throw new UsageException("Provide --token, or --email together with --password.");
            }
        }

        if (chunkSize is { } chunk && (chunk < Transfer.ChunkPlan.MinChunkSizeMiB || chunk > Transfer.ChunkPlan.MaxChunkSizeMiB))
        {
            throw new UsageException($"--chunk-size-mib must be between {Transfer.ChunkPlan.MinChunkSizeMiB} and {Transfer.ChunkPlan.MaxChunkSizeMiB}.");
        }

        if (concurrency is { } parallel && (parallel < 1 || parallel > 6))
        {
            throw new UsageException("--concurrency must be between 1 and 6.");
        }

        if (bandwidth is { } mbps && (mbps < 1 || mbps > 1000))
        {
            throw new UsageException("--max-bandwidth-mbps must be between 1 and 1000.");
        }

        if (maxRetries is { } retries && retries < 0)
        {
            throw new UsageException("--max-retries cannot be negative.");
        }

        return new CliOptions
        {
            Command = command,
            ServerUrl = server,
            FilePath = file,
            VaultSlug = vault,
            Email = email,
            Password = password,
            AccessToken = token,
            ChunkSizeMiB = chunkSize,
            Concurrency = concurrency,
            MaxBandwidthMbps = bandwidth,
            NoCompress = flags.Contains("no-compress"),
            StateDbPath = stateDb,
            MaxRetries = maxRetries,
            NoResume = flags.Contains("no-resume"),
            JsonProgress = flags.Contains("json-progress"),
            Adaptive = flags.Contains("adaptive"),
            UploadId = uploadId,
        };
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new UsageException($"Flag '--{name}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static string? Get(Dictionary<string, string> values, string name)
        => values.TryGetValue(name, out string? value) ? value : null;

    private static int? GetInt(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out string? raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new UsageException($"Flag '--{name}' expects an integer, got '{raw}'.");
        }

        return value;
    }
}
