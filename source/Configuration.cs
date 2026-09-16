using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata.Airing;

namespace Shoko.Plugin.AnimeSchedule;

/// <summary>
/// Configuration for the AnimeSchedule.net airing schedule provider.
/// </summary>
/// <remarks>
/// AnimeSchedule.net rate-limits requests per application <em>and</em> per IP
/// address (120 requests/minute each), so a token bundled with the plugin
/// would be shared, and therefore throttled, by every install that uses it.
/// Each user is expected to register their own application at
/// https://animeschedule.net/api/v3/documentation/apps and paste its token
/// here.
/// </remarks>
[Display(Name = "AnimeSchedule.net")]
public class Configuration : IAiringScheduleProviderConfiguration, INewtonsoftJsonConfiguration
{
    /// <summary>
    /// Your own AnimeSchedule.net application token, used as a Bearer token
    /// on every request. Required; the provider refuses to run without one.
    /// </summary>
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "App Token", Description = "Your personal AnimeSchedule.net application token. Register an app at animeschedule.net/api/v3/documentation/apps to get one.")]
    public string? AppToken { get; set; }
}
