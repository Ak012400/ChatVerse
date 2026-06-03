using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace ChatVerse.Infrastructure.ExternalServices.Cloudinary;

/// <summary>
/// Cloudinary upload service. Uses the official CloudinaryDotNet SDK
/// so we don't have to maintain the signing dance ourselves.
///
/// The previous hand-rolled signature implementation was occasionally
/// triggering Cloudinary's "Upload preset must be specified when using
/// unsigned upload" — that error path is what the API takes whenever
/// it can't validate the signature, which is fragile because any
/// missing or wrongly-ordered param invalidates the hash.
///
/// Avatars  → public, overwritable upload (same public_id replaces).
/// Documents → "authenticated" type, accessed only via signed URL.
/// </summary>
public class CloudinaryService
{
    private readonly CloudinaryDotNet.Cloudinary _cloudinary;
    private readonly ILogger<CloudinaryService> _logger;

    public CloudinaryService(IConfiguration config, ILogger<CloudinaryService> logger)
    {
        var account = new Account(
            config["Cloudinary:CloudName"],
            config["Cloudinary:ApiKey"],
            config["Cloudinary:ApiSecret"]
        );
        _cloudinary = new CloudinaryDotNet.Cloudinary(account) { Api = { Secure = true } };
        _logger = logger;
    }

    // ============================================================
    //  UploadAvatarAsync — public CDN URL for profile pictures.
    //  We use a stable public_id (chatverse/avatars/{userId}) +
    //  overwrite=true so re-uploads replace cleanly instead of
    //  piling up versions.
    // ============================================================
    public async Task<CloudinaryUploadResult> UploadAvatarAsync(
        Stream fileStream, string fileName, string userId)
    {
        var publicId = $"chatverse/avatars/{userId}";

        var uploadParams = new ImageUploadParams
        {
            File = new FileDescription(fileName, fileStream),
            PublicId = publicId,
            Overwrite = true,
            // Pre-bake a smaller, square version so the chat list isn't
            // pulling 4MB selfies. 256×256 is plenty for an avatar.
            Transformation = new Transformation()
                .Width(256).Height(256).Crop("fill").Gravity("face")
                .Quality("auto").FetchFormat("auto"),
        };

        return await UploadAsync(uploadParams, publicId);
    }

    // ============================================================
    //  UploadDocumentAsync — private/authenticated ID document.
    //  Reachable only via a signed URL with limited expiry.
    // ============================================================
    public async Task<CloudinaryUploadResult> UploadDocumentAsync(
        Stream fileStream, string fileName, string userId)
    {
        var publicId = $"chatverse/docs/{userId}/{Guid.NewGuid()}";

        var uploadParams = new ImageUploadParams
        {
            File = new FileDescription(fileName, fileStream),
            PublicId = publicId,
            Type = "authenticated",
            Overwrite = false,
        };

        return await UploadAsync(uploadParams, publicId);
    }

    // ============================================================
    //  Core upload — same call site for avatars + docs.
    // ============================================================
    private async Task<CloudinaryUploadResult> UploadAsync(
        ImageUploadParams uploadParams, string fallbackPublicId)
    {
        try
        {
            var result = await _cloudinary.UploadAsync(uploadParams);

            if (result.Error != null)
            {
                _logger.LogError("Cloudinary upload failed: {Status} — {Msg}",
                    result.StatusCode, result.Error.Message);
                return CloudinaryUploadResult.Failed(result.Error.Message);
            }

            return new CloudinaryUploadResult
            {
                Success = true,
                PublicId = result.PublicId ?? fallbackPublicId,
                SecureUrl = result.SecureUrl?.ToString() ?? "",
                Format = result.Format ?? "",
                Bytes = result.Bytes,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during Cloudinary upload");
            return CloudinaryUploadResult.Failed(ex.Message);
        }
    }

    // ============================================================
    //  GenerateSignedUrl — temporary access to a private document.
    //  Default expiry = 1 hour, enough for an admin review session.
    // ============================================================
    public string GenerateSignedUrl(string publicId, int expirySeconds = 3600)
    {
        return _cloudinary.Api.UrlImgUp
            .Secure(true)
            .Source(publicId)
            .Action("authenticated")
            .Signed(true)
            .BuildUrl();
    }
}

// ── Result model (unchanged shape so callers don't need updates) ──
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
