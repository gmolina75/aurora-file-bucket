using AuroraFileBucket.Client.Cli;
using AuroraFileBucket.Client.Commands;
using AuroraFileBucket.Client.Protocol;
using AuroraFileBucket.Client.Transfer;

namespace AuroraFileBucket.Client;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            CliOptions options = CliParser.Parse(args);
            if (options.Command == CommandKind.Help)
            {
                Console.WriteLine(CliParser.HelpText);
                return ExitCodes.Success;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            return options.Command == CommandKind.Status
                ? await new StatusCommand(options).RunAsync(cancellation.Token).ConfigureAwait(false)
                : await new UploadCommand(options).RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliParser.HelpText);
            return ExitCodes.InvalidUsage;
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine($"error: {(string.IsNullOrEmpty(ex.ErrorCode) ? "http" : ex.ErrorCode)}: {ex.Message}");
            return ExitCodes.ForStatus(ex.StatusCode);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: the transfer was cancelled.");
            return ExitCodes.Failure;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Network;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException
                                      or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
                                      or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.LocalState;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
