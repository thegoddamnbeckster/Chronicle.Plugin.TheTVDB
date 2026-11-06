using System.Text.RegularExpressions;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.TheTVDB.Tests;

public class ProviderTests
{
    private static readonly TheTvdbMetadataProvider P = new();

    [Fact]
    public void The_api_key_is_a_required_hidden_setting_with_help_text()
    {
        var key = P.GetSettingsSchema().Settings.Single(s => s.Key == "api_key");
        Assert.Equal(SettingType.Password, key.Type);
        Assert.True(key.Required);
        Assert.False(string.IsNullOrWhiteSpace(key.Description));
    }

    [Fact]
    public void Every_setting_has_help_text_and_every_secret_is_hidden()
    {
        foreach (var s in P.GetSettingsSchema().Settings.Where(s => s.Type != SettingType.Notice))
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Description), s.Key);
            if (Regex.IsMatch(s.Key, "key|token|secret|password", RegexOptions.IgnoreCase))
                Assert.Equal(SettingType.Password, s.Type);
        }
    }

    private static readonly HashSet<string> HostKnows = new HashSet<string> { "backdrop_url", "banner_url", "birth_date", "bpm", "cast", "character_art_url", "clearart_url", "collection", "composer", "crew", "death_date", "disc_url", "genres", "isrc", "label", "language", "logo_url", "mood", "overview", "poster_url", "rating", "runtime_minutes", "tags", "thumb_url", "title", "url", "year", "year" };

    [Fact]
    public void Every_field_it_declares_is_one_the_host_can_resolve()
    {
        // A field the host does not know shows nowhere on Metadata Assignment, so declaring it does nothing.
        foreach (var t in new TheTvdbMetadataProvider().GetSupportedMediaTypes())
        {
            Assert.All(t.SupportedFields, f => Assert.Contains(f, HostKnows));
            foreach (var (_, fields) in t.LevelFields ?? [])
                Assert.All(fields, f => Assert.Contains(f, HostKnows));
        }
    }
}
