namespace Synap.Application.Features.Users;

/// <summary>Bound from "App" - PublicBaseUrl is the web app's URL, used in links sent by email.</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    public string PublicBaseUrl { get; set; } = "http://localhost:4200";
}
