using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatVerse.Infrastructure.ExternalServices.Cloudinary;

/// <summary>
/// Cloudinary upload service.
/// Avatars  → public upload, CDN URL returned
/// Documents → private/authenticated upload for age verification
/// </summary>
public class CloudinaryService
{
    private readonly HttpClient _http;
    private readonly string _cloudName;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly ILogger<CloudinaryService> _logger;

    public CloudinaryService(
        HttpClient http,
        IConfiguration config,
        ILogger<CloudinaryService> logger)
    {
        _http = http;
        _cloudName = config["Cloudinary:CloudName"]!;
        _apiKey = config["Cloudinary:ApiKey"]!;
        _apiSecret = config["Cloudinary:ApiSecret"]!;
        _logger = logger;
    }

    // ============================================================
    //  UploadAvatarAsync
    //  Public upload — CDN URL returned for profile picture
    // ============================================================
    public async Task<CloudinaryUploadResult> UploadAvatarAsync(
        Stream fileStream, string fileName, string userId)
    {
        var publicId = $"chatverse/avatars/{userId}";
        return await UploadAsync(fileStream, fileName, publicId, isPrivate: false);
    }

    // ============================================================
    //  UploadDocumentAsync
    //  Private upload — for age verification documents
    //  Access requires signed URL — never publicly accessible
    // ============================================================
    public async Task<CloudinaryUploadResult> UploadDocumentAsync(
        Stream fileStream, string fileName, string userId)
    {
        var publicId = $"chatverse/docs/{userId}/{Guid.NewGuid()}";
        return await UploadAsync(fileStream, fileName, publicId, isPrivate: true);
    }

    // ============================================================
    //  Core upload method
    // ============================================================
    private async Task<CloudinaryUploadResult> UploadAsync(
        Stream fileStream, string fileName, string publicId, bool isPrivate)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = GenerateSignature(publicId, timestamp, isPrivate);

            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(fileStream), "file", fileName);
            form.Add(new StringContent(publicId), "public_id");
            form.Add(new StringContent(timestamp), "timestamp");
            form.Add(new StringContent(_apiKey), "api_key");
            form.Add(new StringContent(signature), "signature");

            if (isPrivate)
                form.Add(new StringContent("authenticated"), "type");

            var url = $"https://api.cloudinary.com/v1_1/{_cloudName}/auto/upload";
            var response = await _http.PostAsync(url, form);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Cloudinary upload failed: {Status} — {Body}",
                    response.StatusCode, body);
                return CloudinaryUploadResult.Failed("Upload failed");
            }

            var result = JsonSerializer.Deserialize<CloudinaryResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new CloudinaryUploadResult
            {
                Success = true,
                PublicId = result?.PublicId ?? publicId,
                SecureUrl = result?.SecureUrl ?? "",
                Format = result?.Format ?? "",
                Bytes = result?.Bytes ?? 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during Cloudinary upload");
            return CloudinaryUploadResult.Failed(ex.Message);
        }
    }

    // ============================================================
    //  GenerateSignedUrl
    //  For private documents — generates temporary access URL
    // ============================================================
    public string GenerateSignedUrl(string publicId, int expirySeconds = 3600)
    {
        var expireAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expirySeconds;
        var signature = GenerateSignature(publicId, expireAt.ToString(), true);

        return $"https://res.cloudinary.com/{_cloudName}/image/authenticated/" +
               $"s--{signature[..8]}--/e_expires:{expireAt}/{publicId}";
    }

    // ── SHA1 signature for Cloudinary API ────────────────────
    private string GenerateSignature(string publicId, string timestamp, bool isPrivate)
    {
        var toSign = $"public_id={publicId}&timestamp={timestamp}";
        if (isPrivate) toSign += "&type=authenticated";
        toSign += _apiSecret;

        using var sha1 = SHA1.Create();
        var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(toSign));
        return Convert.ToHexString(bytes).ToLower();
    }
}

// ── Result + response models ──────────────────────────────────
public class CloudinaryUploadResult
{
    public bool Success { get; set; }
    public string PublicId { get; set; } = "";
    public string SecureUrl { get; set; } = "";
    public string Format { get; set; } = "";
    public long Bytes { get; set; }
    public string? Error { get; set; }

    public static CloudinaryUploadResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };
}

public class CloudinaryResponse
{
    public string? PublicId { get; set; }
    public string? SecureUrl { get; set; }
    public string? Format { get; set; }
    public long Bytes { get; set; }
}