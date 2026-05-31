namespace ChatVerse.API.Models;

public class VideoSettings
{
    public bool EnableAgeBypass { get; set; }
    public int MinimumTrustScoreRequired { get; set; }
    public string RequiredTier { get; set; } = "Basic";
}