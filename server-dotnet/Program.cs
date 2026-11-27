using System.Text.Json;
using AuroraFileBucket.Server.Configuration;
using AuroraFileBucket.Server.Data;
using AuroraFileBucket.Server.Endpoints;
using AuroraFileBucket.Server.Infrastructure;
using AuroraFileBucket.Server.Services;
using AuroraFileBucket.Server.Services.Notifications;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// HTTP endpoint. ASPNETCORE_URLS and --urls win; otherwise the receiver listens
// on http://localhost:5080.
// ---------------------------------------------------------------------------
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5080");
}

using var bootstrapLoggerFactory = LoggerFactory.Create(logging =>
    logging.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole());
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("AuroraFileBucket.Server");

// ---------------------------------------------------------------------------
// Configuration. The standard options pattern is used, so every value can be
// overridden with environment variables using the double underscore separator,
// e.g. AuroraStorage__StagingRoot or Auth__Users__0__Email.
// ---------------------------------------------------------------------------
var storageOptions = builder.Configuration.GetSection(AuroraStorageOptions.SectionName).Get<AuroraStorageOptions>()
    ?? new AuroraStorageOptions();
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
    ?? new AuthOptions();

builder.Services.Configure<AuroraStorageOptions>(builder.Configuration.GetSection(AuroraStorageOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<NotificationsOptions>(builder.Configuration.GetSection(NotificationsOptions.SectionName));

// The effective layout is resolved from the options on first use, so the receiver
// always starts, even without any configuration at all.
builder.Services.AddSingleton(serviceProvider => StorageLayout.Resolve(
    serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuroraStorageOptions>>().Value,
    serviceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
    serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AuroraFileBucket.Server.Storage")));

// The signing key is read once at startup: an empty value means an ephemeral key.
var signingKey = SigningKey.Resolve(authOptions, bootstrapLogger);
builder.Services.AddSingleton(signingKey);

builder.Services.AddSingleton(TimeProvider.System);

// ---------------------------------------------------------------------------
// Services.
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<AuroraDatabase>();
builder.Services.AddSingleton<UploadRepository>();
builder.Services.AddSingleton<RefreshTokenRepository>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AssemblyQueue>();
builder.Services.AddSingleton<VaultQuotaService>();
builder.Services.AddSingleton<DiskPressureMonitor>();
builder.Services.AddSingleton<IProgressPublisher, NullProgressPublisher>();
builder.Services.AddSingleton<INotifier, NullNotifier>();

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<AssemblyWorker>();
builder.Services.AddHostedService<OrphanPurgeService>();

// ---------------------------------------------------------------------------
// Kestrel. The body limit is the biggest allowed chunk plus a little head room,
// the default of 30 MB would reject 32 MiB chunks.
// ---------------------------------------------------------------------------
var maxChunkBytes = (long)storageOptions.MaxChunkSizeMiB * 1024 * 1024;
builder.WebHost.ConfigureKestrel((context, options) =>
{
    var configured = context.Configuration.GetSection(AuroraStorageOptions.SectionName).Get<AuroraStorageOptions>();
    var maxChunkMiB = configured is { MaxChunkSizeMiB: > 0 } ? configured.MaxChunkSizeMiB : 32;
    options.Limits.MaxRequestBodySize = ((long)maxChunkMiB * 1024 * 1024) + (1024 * 1024);
    options.AddServerHeader = false;
});

// ---------------------------------------------------------------------------
// Authentication: short lived HS256 access tokens.
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(signingKey.Key),
            ValidateIssuer = !string.IsNullOrWhiteSpace(authOptions.JwtIssuer),
            ValidIssuer = authOptions.JwtIssuer,
            ValidateAudience = !string.IsNullOrWhiteSpace(authOptions.JwtAudience),
            ValidAudience = authOptions.JwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name"
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await ApiError.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    ApiError.Codes.Unauthorized,
                    "A valid access token is required.");
            },
            OnForbidden = async context =>
            {
                await ApiError.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status403Forbidden,
                    ApiError.Codes.Forbidden,
                    "The token does not allow this operation.");
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<IProblemDetailsService, AuroraProblemDetailsService>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline.
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The client went away: nothing to report.
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled exception while processing {Method} {Path}.", context.Request.Method, context.Request.Path);
        if (!context.Response.HasStarted)
        {
            await ApiError.WriteAsync(context, StatusCodes.Status500InternalServerError, ApiError.Codes.InternalError, "Unexpected server error.");
        }
    }
});

app.UseStatusCodePages(async statusCodeContext =>
{
    var context = statusCodeContext.HttpContext;
    var status = context.Response.StatusCode;
    var (code, message) = status switch
    {
        StatusCodes.Status400BadRequest => (ApiError.Codes.InvalidRequest, "The request could not be parsed."),
        StatusCodes.Status401Unauthorized => (ApiError.Codes.Unauthorized, "A valid access token is required."),
        StatusCodes.Status403Forbidden => (ApiError.Codes.Forbidden, "The token does not allow this operation."),
        StatusCodes.Status404NotFound => (ApiError.Codes.NotFound, "Resource not found."),
        StatusCodes.Status405MethodNotAllowed => (ApiError.Codes.InvalidRequest, "Method not allowed."),
        StatusCodes.Status413PayloadTooLarge => (ApiError.Codes.QuotaExceeded, "Payload too large."),
        StatusCodes.Status429TooManyRequests => (ApiError.Codes.Backpressure, "Server is busy, retry later."),
        _ => (ApiError.Codes.InternalError, "Unexpected server error.")
    };

    await ApiError.WriteAsync(context, status, code, message);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapUploadEndpoints();

var effectiveLayout = app.Services.GetRequiredService<StorageLayout>();
app.Logger.LogInformation(
    "Aurora File Bucket receiver v0.1.0 ready. Staging: {StagingRoot}; vaults: {VaultsRoot}; database: {DatabasePath}.",
    effectiveLayout.StagingRoot,
    effectiveLayout.VaultsRoot,
    effectiveLayout.DatabasePath);

app.Run();

/// <summary>Entry point marker so the test project can host the application.</summary>
public partial class Program
{
}
