namespace SteamVault.Models;

public class WhatsNewItem
{
    public string Tag { get; set; } = "NEW"; // NEW, PERFORMANCE, IMPROVED, THEME, FIXED
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public string BadgeBackground => Tag.ToUpperInvariant() switch
    {
        "NEW" => "#2E412E",
        "PERFORMANCE" => "#3B2844",
        "SPEED" => "#3B2844",
        "IMPROVED" => "#1F3448",
        "THEME" => "#3D3520",
        "FIXED" => "#442E28",
        _ => "#2D3139"
    };

    public string BadgeForeground => Tag.ToUpperInvariant() switch
    {
        "NEW" => "#A6E22E",
        "PERFORMANCE" => "#BD93F9",
        "SPEED" => "#BD93F9",
        "IMPROVED" => "#66D9EF",
        "THEME" => "#E6DB74",
        "FIXED" => "#FF922B",
        _ => "#F8F8F2"
    };
}
