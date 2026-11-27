using System.Net;
using System.Net.Http.Json;
using AuroraFileBucket.Server.Contracts;
using Xunit;

namespace AuroraFileBucket.Server.Tests;

/// <summary>Protocol level tests of the receiver, driven through real HTTP calls.</summary>
public sealed class ProtocolTests : IClassFixture<AuroraAppFactory>
{
    private readonly AuroraAppFactory _factory;
    private readonly HttpClient _client;

    public ProtocolTests(AuroraAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // -----------------------------------------------------------------------
    // Authentication
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Login_Returns_TokenPair()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/auth/login",
            new { email = AuroraAppFactory.DevEmail, password = AuroraAppFactory.DevPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var pair = await response.Content.ReadFromJsonAsync<TokenPair>();
        Assert.NotNull(pair);
        Assert.Equal("Bearer", pair!.TokenType);
        Assert.False(string.IsNullOrWhiteSpace(pair.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(pair.RefreshToken));
        Assert.Equal(1200, pair.ExpiresIn);
        Assert.Equal(AuroraAppFactory.DevEmail, pair.User.Email);
        Assert.Equal("Dev User", pair.User.DisplayName);
        Assert.Contains("default", pair.Vaults);
    }

    [Fact]
    public async Task Login_With_Wrong_Password_Returns_401_InvalidCredentials()
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/auth/login",
            new { email = AuroraAppFactory.DevEmail, password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", await TestHelpers.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Login_Without_Token_Returns_401_Envelope()
    {
        var response = await _client.GetAsync("/v1/uploads/00000000-0000-0000-0000-000000000000/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await TestHelpers.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Refresh_Is_Single_Use_And_Revokes_Every_Token_Of_The_User()
    {
        var pair = await TestHelpers.LoginAsync(_client);

        var first = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = pair.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = await first.Content.ReadFromJsonAsync<TokenPair>();
        Assert.NotEqual(pair.RefreshToken, rotated!.RefreshToken);

        // Replaying the old token is a reuse: the server answers 401.
        var replay = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = pair.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_refresh_token", await TestHelpers.ErrorCodeAsync(replay));

        // The reuse also revoked the token that was just issued.
        var afterRevoke = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
        Assert.Equal("invalid_refresh_token", await TestHelpers.ErrorCodeAsync(afterRevoke));
    }

    // -----------------------------------------------------------------------
    // Manifest validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Init_With_Mismatched_TotalParts_Returns_400_InvalidManifest()
    {
        var pair = await TestHelpers.LoginAsync(_client);

        var response = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            "movie.mp4",
            10 * TestHelpers.MiB,
            4 * TestHelpers.MiB,
            99,
            null,
            "auto"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_manifest", await TestHelpers.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Init_With_ChunkSize_Out_Of_Range_Returns_400_InvalidManifest()
    {
        var pair = await TestHelpers.LoginAsync(_client);

        var tooSmall = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            "movie.mp4",
            10 * TestHelpers.MiB,
            1024 * 1024,
            10,
            null,
            "auto"));
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);
        Assert.Equal("invalid_manifest", await TestHelpers.ErrorCodeAsync(tooSmall));

        var tooBig = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            "movie.mp4",
            64 * TestHelpers.MiB,
            64 * TestHelpers.MiB,
            1,
            null,
            "auto"));
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal("invalid_manifest", await TestHelpers.ErrorCodeAsync(tooBig));
    }

    [Fact]
    public async Task Init_With_Forbidden_Vault_Returns_403_VaultNotAllowed()
    {
        var pair = await TestHelpers.LoginAsync(_client);

        var response = await TestHelpers.InitAsync(
            _client,
            pair.AccessToken,
            new InitRequest("movie.mp4", 8 * TestHelpers.MiB, 4 * TestHelpers.MiB, 2, null, "auto"),
            vaultSlug: "other");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("vault_not_allowed", await TestHelpers.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Init_Returns_201_And_Truncates_The_Missing_Parts()
    {
        var pair = await TestHelpers.LoginAsync(_client);

        var response = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            "big.bin",
            6400L * 4 * TestHelpers.MiB,
            4 * TestHelpers.MiB,
            6400,
            null,
            "auto"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<UploadSessionCreated>();
        Assert.NotNull(session);
        Assert.Equal("INIT", session!.Status);
        Assert.Equal("default", session.VaultSlug);
        Assert.Empty(session.ReceivedParts);
        Assert.Equal(100, session.MissingParts.Count);
        Assert.Equal(1, session.MissingParts[0]);
        Assert.Equal(100, session.MissingParts[99]);
    }

    // -----------------------------------------------------------------------
    // Chunk ingest
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PutPart_With_Wrong_Hash_Returns_400_ChunkHashMismatch()
    {
        var (pair, uploadId) = await StartSessionAsync("hash-mismatch.bin", 4 * TestHelpers.MiB);

        var body = TestHelpers.Pattern(4 * TestHelpers.MiB, 7);
        using var message = new HttpRequestMessage(HttpMethod.Put, $"/v1/uploads/{uploadId}/parts/1")
        {
            Content = new ByteArrayContent(body)
        };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pair.AccessToken);
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        message.Headers.Add("X-Chunk-Sha256", new string('a', 64));
        message.Headers.Add("X-Raw-Size", body.Length.ToString());
        message.Headers.Add("X-Compression", "none");

        var response = await _client.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("chunk_hash_mismatch", await TestHelpers.ErrorCodeAsync(response));

        // The rejected chunk is not left behind in the staging folder.
        var staged = Path.Combine(_factory.StagingRoot, uploadId, "part_00001.bin");
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task PutPart_Without_ChunkSha256_Returns_400_InvalidRequest()
    {
        var (pair, uploadId) = await StartSessionAsync("no-hash.bin", 4 * TestHelpers.MiB);

        var body = TestHelpers.Pattern(1024, 1);
        var response = await TestHelpers.PutPartAsync(
            _client,
            pair.AccessToken,
            uploadId,
            1,
            body,
            omitChunkSha256: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await TestHelpers.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task PutPart_Without_RawSize_Returns_400_InvalidRequest()
    {
        var (pair, uploadId) = await StartSessionAsync("no-raw-size.bin", 4 * TestHelpers.MiB);

        var response = await TestHelpers.PutPartAsync(
            _client,
            pair.AccessToken,
            uploadId,
            1,
            TestHelpers.Pattern(1024, 2),
            omitRawSize: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await TestHelpers.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task PutPart_With_Chunked_Encoding_Returns_400_InvalidRequest()
    {
        var (pair, uploadId) = await StartSessionAsync("chunked.bin", 4 * TestHelpers.MiB);

        var response = await TestHelpers.PutPartAsync(
            _client,
            pair.AccessToken,
            uploadId,
            1,
            TestHelpers.Pattern(1024, 3),
            omitContentLength: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await TestHelpers.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task PutPart_With_Different_Hash_Returns_409_PartConflict()
    {
        var (pair, uploadId) = await StartSessionAsync("conflict.bin", 4 * TestHelpers.MiB);

        var body = TestHelpers.Pattern(4 * TestHelpers.MiB, 9);
        var first = await TestHelpers.PutPartAsync(_client, pair.AccessToken, uploadId, 1, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Same part number, different content.
        var other = TestHelpers.Pattern(4 * TestHelpers.MiB, 10);
        var second = await TestHelpers.PutPartAsync(_client, pair.AccessToken, uploadId, 1, other);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("part_conflict", await TestHelpers.ErrorCodeAsync(second));
    }

    [Fact]
    public async Task PutPart_With_Same_Hash_Is_Idempotent()
    {
        var (pair, uploadId) = await StartSessionAsync("idempotent.bin", 4 * TestHelpers.MiB);

        var body = TestHelpers.Pattern(4 * TestHelpers.MiB, 11);
        var first = await TestHelpers.PutPartAsync(_client, pair.AccessToken, uploadId, 1, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var receipt = await first.Content.ReadFromJsonAsync<PartReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal(1, receipt!.PartNumber);
        Assert.Equal(1, receipt.ReceivedParts);
        Assert.Equal("none", receipt.Compression);
        Assert.Equal(body.Length, receipt.CompressedSize);
        Assert.Equal("UPLOADING", receipt.Status);

        var second = await TestHelpers.PutPartAsync(_client, pair.AccessToken, uploadId, 1, body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Status_Reports_Received_And_Missing_Parts()
    {
        var (pair, uploadId) = await StartSessionAsync("status.bin", 8 * TestHelpers.MiB);

        var response = await TestHelpers.PutPartAsync(
            _client,
            pair.AccessToken,
            uploadId,
            1,
            TestHelpers.Pattern(4 * TestHelpers.MiB, 21));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await TestHelpers.GetStatusAsync(_client, pair.AccessToken, uploadId);
        Assert.Equal("UPLOADING", status.Status);
        Assert.Equal(new[] { 1 }, status.ReceivedParts);
        Assert.Equal(new[] { 2 }, status.MissingParts);
        Assert.Equal(4 * TestHelpers.MiB, status.BytesReceived);
        Assert.Equal(8 * TestHelpers.MiB, status.TotalBytes);
        Assert.Equal(2, status.TotalParts);
        Assert.Null(status.CompletedAt);
        Assert.Null(status.VaultPath);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Complete_With_Missing_Parts_Returns_409_IncompleteUpload()
    {
        var (pair, uploadId) = await StartSessionAsync("incomplete.bin", 8 * TestHelpers.MiB);

        var response = await TestHelpers.PutPartAsync(
            _client,
            pair.AccessToken,
            uploadId,
            1,
            TestHelpers.Pattern(4 * TestHelpers.MiB, 31));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var complete = await TestHelpers.CompleteAsync(_client, pair.AccessToken, uploadId);
        Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        Assert.Equal("incomplete_upload", await TestHelpers.ErrorCodeAsync(complete));
    }

    // -----------------------------------------------------------------------
    // Assembly
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_Assembles_The_File_And_Cleans_The_Staging_Folder()
    {
        var first = TestHelpers.Pattern(4 * TestHelpers.MiB, 41);
        var second = TestHelpers.Pattern(1 * TestHelpers.MiB, 42);
        var expectedHash = TestHelpers.Sha256OfParts(new[] { first, second });
        var fileName = $"happy-{Guid.NewGuid():N}.bin";

        var pair = await TestHelpers.LoginAsync(_client);
        var init = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            fileName,
            5 * TestHelpers.MiB,
            4 * TestHelpers.MiB,
            2,
            null,
            "none"));
        Assert.Equal(HttpStatusCode.Created, init.StatusCode);
        var session = await init.Content.ReadFromJsonAsync<UploadSessionCreated>();
        var uploadId = session!.UploadId;

        var part1 = await TestHelpers.PutPartAsync(_client, pair.AccessToken, uploadId, 1, first);
        Assert.Equal(HttpStatusCode.OK, part1.StatusCode);
        var part2 = await TestHelpers.PutPartAsync(_client, pair.AccessToken, uploadId, 2, second);
        Assert.Equal(HttpStatusCode.OK, part2.StatusCode);

        var complete = await TestHelpers.CompleteAsync(_client, pair.AccessToken, uploadId, expectedHash);
        Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);
        var accepted = await complete.Content.ReadFromJsonAsync<AssemblyAccepted>();
        Assert.NotNull(accepted);
        Assert.Equal("ASSEMBLING", accepted!.Status);
        Assert.Equal($"/v1/uploads/{uploadId}/status", accepted.StatusUrl);

        var final = await TestHelpers.WaitForFinalStateAsync(_client, pair.AccessToken, uploadId, TimeSpan.FromSeconds(30));

        Assert.Equal("READY", final.Status);
        Assert.Equal(expectedHash, final.FinalSha256);
        Assert.NotNull(final.CompletedAt);
        Assert.NotNull(final.VaultPath);

        // The file is in the vault with the expected content.
        var vaultFile = Path.Combine(_factory.VaultPath("default"), fileName);
        Assert.True(File.Exists(vaultFile));
        Assert.Equal(expectedHash, TestHelpers.Sha256Hex(File.ReadAllBytes(vaultFile)));
        Assert.Equal(5 * TestHelpers.MiB, new FileInfo(vaultFile).Length);

        // The staging folder was removed right after the assembly.
        Assert.False(Directory.Exists(Path.Combine(_factory.StagingRoot, uploadId)));
    }

    [Fact]
    public async Task HappyPath_With_Zstd_Compression_Stores_The_Original_Bytes()
    {
        var raw = TestHelpers.Pattern(4 * TestHelpers.MiB, 51);
        var compressed = TestHelpers.ZstdCompress(raw);
        Assert.True(compressed.Length < raw.Length);

        var expectedHash = TestHelpers.Sha256Hex(raw);
        var fileName = $"zstd-{Guid.NewGuid():N}.bin";

        var pair = await TestHelpers.LoginAsync(_client);
        var init = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            fileName,
            4 * TestHelpers.MiB,
            4 * TestHelpers.MiB,
            1,
            expectedHash,
            "auto"));
        Assert.Equal(HttpStatusCode.Created, init.StatusCode);
        var session = await init.Content.ReadFromJsonAsync<UploadSessionCreated>();

        var part = await TestHelpers.PutPartAsync(
            _client,
            pair.AccessToken,
            session!.UploadId,
            1,
            compressed,
            compression: "zstd",
            rawSha256: expectedHash);

        Assert.Equal(HttpStatusCode.OK, part.StatusCode);

        var complete = await TestHelpers.CompleteAsync(_client, pair.AccessToken, session.UploadId);
        Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);

        var final = await TestHelpers.WaitForFinalStateAsync(_client, pair.AccessToken, session.UploadId, TimeSpan.FromSeconds(30));
        Assert.Equal("READY", final.Status);
        Assert.Equal(expectedHash, final.FinalSha256);

        var vaultFile = Path.Combine(_factory.VaultPath("default"), fileName);
        Assert.True(File.Exists(vaultFile));
        Assert.Equal(expectedHash, TestHelpers.Sha256Hex(File.ReadAllBytes(vaultFile)));
    }

    [Fact]
    public async Task Complete_With_Wrong_Global_Hash_Fails_With_GlobalHashMismatch()
    {
        var body = TestHelpers.Pattern(4 * TestHelpers.MiB, 61);

        var pair = await TestHelpers.LoginAsync(_client);
        var init = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            $"mismatch-{Guid.NewGuid():N}.bin",
            4 * TestHelpers.MiB,
            4 * TestHelpers.MiB,
            1,
            null,
            "none"));
        var session = await init.Content.ReadFromJsonAsync<UploadSessionCreated>();

        var part = await TestHelpers.PutPartAsync(_client, pair.AccessToken, session!.UploadId, 1, body);
        Assert.Equal(HttpStatusCode.OK, part.StatusCode);

        var complete = await TestHelpers.CompleteAsync(_client, pair.AccessToken, session.UploadId, new string('b', 64));
        Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);

        var final = await TestHelpers.WaitForFinalStateAsync(_client, pair.AccessToken, session.UploadId, TimeSpan.FromSeconds(30));
        Assert.Equal("FAILED", final.Status);
        Assert.NotNull(final.Error);
        Assert.Equal("global_hash_mismatch", final!.Error!.Code);

        // Nothing was published in the vault and the temporary file is gone.
        var vaultFolder = _factory.VaultPath("default");
        Assert.Empty(Directory.GetFiles(vaultFolder, "*.assembling"));
    }

    [Fact]
    public async Task Assembly_Resolves_An_Existing_File_Name_With_The_Upload_Id()
    {
        var body = TestHelpers.Pattern(4 * TestHelpers.MiB, 71);
        var fileName = "duplicate.bin";

        var pair = await TestHelpers.LoginAsync(_client);

        // First upload with the same name.
        var firstInit = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            fileName, 4 * TestHelpers.MiB, 4 * TestHelpers.MiB, 1, null, "none"));
        var firstSession = await firstInit.Content.ReadFromJsonAsync<UploadSessionCreated>();
        await TestHelpers.PutPartAsync(_client, pair.AccessToken, firstSession!.UploadId, 1, body);
        await TestHelpers.CompleteAsync(_client, pair.AccessToken, firstSession.UploadId, TestHelpers.Sha256Hex(body));
        var firstFinal = await TestHelpers.WaitForFinalStateAsync(_client, pair.AccessToken, firstSession.UploadId, TimeSpan.FromSeconds(30));
        Assert.Equal("READY", firstFinal.Status);
        Assert.Equal(Path.Combine(_factory.VaultPath("default"), fileName), firstFinal.VaultPath);

        // Second upload with the same name: the upload id is inserted before the extension.
        var secondInit = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            fileName, 4 * TestHelpers.MiB, 4 * TestHelpers.MiB, 1, null, "none"));
        var secondSession = await secondInit.Content.ReadFromJsonAsync<UploadSessionCreated>();
        await TestHelpers.PutPartAsync(_client, pair.AccessToken, secondSession!.UploadId, 1, body);
        await TestHelpers.CompleteAsync(_client, pair.AccessToken, secondSession.UploadId, TestHelpers.Sha256Hex(body));
        var secondFinal = await TestHelpers.WaitForFinalStateAsync(_client, pair.AccessToken, secondSession.UploadId, TimeSpan.FromSeconds(30));

        Assert.Equal("READY", secondFinal.Status);
        Assert.Equal(
            Path.Combine(_factory.VaultPath("default"), $"duplicate.{secondSession.UploadId}.bin"),
            secondFinal.VaultPath);
        Assert.True(File.Exists(secondFinal.VaultPath));
    }

    [Fact]
    public async Task Unknown_Upload_Returns_404()
    {
        var pair = await TestHelpers.LoginAsync(_client);

        using var message = new HttpRequestMessage(HttpMethod.Get, $"/v1/uploads/{Guid.NewGuid():D}/status");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pair.AccessToken);
        var notFound = await _client.SendAsync(message);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("not_found", await TestHelpers.ErrorCodeAsync(notFound));
    }

    private async Task<(TokenPair Pair, string UploadId)> StartSessionAsync(string fileName, long totalBytes)
    {
        var pair = await TestHelpers.LoginAsync(_client);
        var response = await TestHelpers.InitAsync(_client, pair.AccessToken, new InitRequest(
            fileName,
            totalBytes,
            4 * TestHelpers.MiB,
            (int)(totalBytes / (4 * TestHelpers.MiB)),
            null,
            "none"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<UploadSessionCreated>();
        return (pair, session!.UploadId);
    }
}
