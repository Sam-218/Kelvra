namespace Kelvra.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --tray", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe /min", "C:\\Program Files\\App\\app.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe")]
    [InlineData("C:\\Tools\\script.cmd", "C:\\Tools\\script.cmd")]
    public void Finds_the_program_in_a_startup_command(string command, string expected) =>
        Assert.Equal(expected, StartupManager.ExeFromCommand(command));

    [Fact]
    public void Startup_command_expands_environment_variables() =>
        Assert.Equal(Environment.ExpandEnvironmentVariables("%WINDIR%\\system32\\x.exe"), StartupManager.ExeFromCommand("%WINDIR%\\system32\\x.exe"));

    [Fact]
    public void Empty_quoted_startup_command_gives_null() => Assert.Null(StartupManager.ExeFromCommand("\"\""));

    [Fact]
    public void Every_place_in_the_time_zone_table_resolves() => Assert.Empty(TimeZonePlaces.Unresolved());

    [Theory]
    [InlineData("Minnesota", "Central Standard Time")]
    [InlineData("Tokyo", "Tokyo Standard Time")]
    [InlineData("Deutschland", "W. Europe Standard Time")]
    [InlineData("America/Chicago", "Central Standard Time")]
    [InlineData("Minneapolis, Minnesota, USA", "Central Standard Time")]
    [InlineData("Minnes", "Central Standard Time")]
    public void Finds_time_zones_for_places(string input, string zone) =>
        Assert.Equal(zone, TimeZonePlaces.Find(input)?.ZoneId);

    [Fact]
    public void Keeps_the_place_name_as_typed_when_it_has_accents() =>
        Assert.Equal("München", TimeZonePlaces.Find("München")?.Label);

    [Theory]
    [InlineData("Qwzxv")]
    [InlineData("   ")]
    public void Unknown_or_empty_places_give_null(string input) => Assert.Null(TimeZonePlaces.Find(input));

    [Fact]
    public void Clock_extras_have_names_and_text()
    {
        Assert.Equal("Tokyo", OverlayExtras.NameOf("extra:tz:Tokyo Standard Time"));
        Assert.Equal("-", OverlayExtras.Text("extra:tz:Nowhere Standard Time"));
        Assert.Null(OverlayExtras.NameOf("/intelcpu/0/temperature/0"));
    }
}
