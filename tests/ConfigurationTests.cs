using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

/// <summary>
/// <see cref="Configuration.AppToken"/> must stay optional: a <c>[Required]</c>
/// property that is unset makes Shoko's configuration validation fail, which
/// would prevent the whole plugin from loading. The plugin must always load
/// and simply stay inactive until the user pastes a token.
/// </summary>
public class ConfigurationTests
{
    private static PropertyInfo AppTokenProperty
        => typeof(Configuration).GetProperty(nameof(Configuration.AppToken))!;

    [Fact]
    public void AppToken_IsNotRequired()
        => Assert.Null(AppTokenProperty.GetCustomAttribute<RequiredAttribute>());

    [Fact]
    public void AppToken_IsStillAPasswordField()
    {
        var dataType = AppTokenProperty.GetCustomAttribute<DataTypeAttribute>();

        Assert.NotNull(dataType);
        Assert.Equal(DataType.Password, dataType.DataType);
    }

    [Fact]
    public void AppToken_StillHasADisplayName()
    {
        var display = AppTokenProperty.GetCustomAttribute<DisplayAttribute>();

        Assert.NotNull(display);
        Assert.Equal("App Token", display.Name);
    }

    [Fact]
    public void AppToken_DefaultsToUnset()
        => Assert.Null(new Configuration().AppToken);
}
