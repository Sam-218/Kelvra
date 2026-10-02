using System.Reflection;
using System.Text.Json;

namespace Kelvra.Tests;

public class DataFormatTests
{
    private static JsonSerializerOptions SettingsJson =>
        (JsonSerializerOptions)typeof(AppSettings).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Escape(string s) =>
        (string)typeof(CsvLogger).GetMethod("Escape", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { s })!;

    [Fact]
    public void Settings_survive_a_json_round_trip()
    {
        var s = new AppSettings { Theme = "Light", RefreshMs = 750, Fahrenheit = true, GroupOrder = { "/a", "/b" } };
        s.Alerts.Add(new AlertRule { SensorId = "x", Threshold = 70.5f, Condition = AlertCondition.Below });
        var overlay = OverlayTemplates.Create("Compact bar", new List<SensorVm>(), "Bar");
        overlay.Items.Add(new OverlayItem { SensorId = "extra:time", Label = "Zeit, \"lokal\"" });
        s.Overlays.Add(overlay);

        string first = JsonSerializer.Serialize(s, SettingsJson);
        string second = JsonSerializer.Serialize(JsonSerializer.Deserialize<AppSettings>(first, SettingsJson), SettingsJson);

        Assert.Equal(first, second);
        Assert.Contains("\"Below\"", first);   // enums as text
        Assert.Contains("\"Horizontal\"", first);
    }

    [Fact]
    public void Wrongly_typed_settings_are_rejected_by_the_serializer() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AppSettings>("{ \"RefreshMs\": \"fast\" }", SettingsJson));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\rb", "\"a\rb\"")] // FS-02
    public void Csv_fields_are_escaped(string input, string expected) => Assert.Equal(expected, Escape(input));
}
