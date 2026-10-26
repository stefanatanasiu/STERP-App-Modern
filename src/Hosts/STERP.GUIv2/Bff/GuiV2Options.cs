namespace Sterp.GuiV2.Bff;

/// <summary>The two existing web services GUIv2 forwards to.</summary>
public enum ForwardTarget
{
    Api,
    Jobs,
}

/// <summary>Configuration section Sterp:GuiV2 (appsettings.json; run-host.ps1 overrides the URLs for a demo environment).</summary>
public sealed class GuiV2Options
{
    public const string Section = "Sterp:GuiV2";

    public string ProductName { get; init; } = "STERP - Supply Chain Management";

    public Uri ApiBaseUrl { get; init; } = new("https://localhost:7101");

    public Uri JobsBaseUrl { get; init; } = new("https://localhost:7102");

    public string ClassicGuiUrl { get; init; } = "https://localhost:7100";

    public string ApiConsoleUrl { get; init; } = "https://localhost:7103";

    /// <summary>Path prefixes of STERP.Api that GUIv2 may call; anything else is answered 404 without leaving the host.</summary>
    public IReadOnlyList<string> ApiForwardPrefixes { get; init; } = [];

    /// <summary>Path prefixes of STERP.Jobs that GUIv2 may call.</summary>
    public IReadOnlyList<string> JobsForwardPrefixes { get; init; } = [];

    public static GuiV2Options From(IConfiguration configuration)
    {
        var s = configuration.GetSection(Section);
        var defaults = new GuiV2Options();
        return new GuiV2Options
        {
            ProductName = s["ProductName"] is { Length: > 0 } name ? name : defaults.ProductName,
            ApiBaseUrl = BaseUri(s["ApiBaseUrl"], defaults.ApiBaseUrl),
            JobsBaseUrl = BaseUri(s["JobsBaseUrl"], defaults.JobsBaseUrl),
            ClassicGuiUrl = s["ClassicGuiUrl"] is { Length: > 0 } gui ? gui.TrimEnd('/') : defaults.ClassicGuiUrl,
            ApiConsoleUrl = s["ApiConsoleUrl"] is { Length: > 0 } console ? console.TrimEnd('/') : defaults.ApiConsoleUrl,
            ApiForwardPrefixes = s.GetSection("ApiForwardPrefixes").Get<string[]>() ?? [],
            JobsForwardPrefixes = s.GetSection("JobsForwardPrefixes").Get<string[]>() ?? [],
        };
    }

    public Uri BaseUrl(ForwardTarget target) => target == ForwardTarget.Jobs ? JobsBaseUrl : ApiBaseUrl;

    public IReadOnlyList<string> Prefixes(ForwardTarget target) => target == ForwardTarget.Jobs ? JobsForwardPrefixes : ApiForwardPrefixes;

    private static Uri BaseUri(string? value, Uri fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var uri = new Uri(value.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new InvalidOperationException($"{Section}: {value} must be an https URL (Windows credentials are sent to it).");
        }
        return uri;
    }
}
