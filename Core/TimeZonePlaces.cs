using System.Globalization;
using System.Text;

namespace Kelvra;

/// <summary>
/// Turns a typed place ("Minnesota", "Tokyo", "Deutschland", "America/Chicago") into a Windows time zone.
/// Offline: a built-in list of countries, US/Canadian/Australian states and big cities, then the Windows zone names.
/// Summer/winter time is handled by <see cref="TimeZoneInfo"/> itself, so a found zone always shows the right time.
/// </summary>
public static class TimeZonePlaces
{
    // "Place|alias|alias=IANA zone". Diacritics and case don't matter when matching.
    // ponytail: hand-kept list, add a line when someone's place is missing
    private const string Table = """
        Alabama=America/Chicago
        Alaska|Anchorage=America/Anchorage
        Arizona|Phoenix=America/Phoenix
        Arkansas=America/Chicago
        California|Los Angeles|LA|San Francisco|San Diego|San Jose=America/Los_Angeles
        Colorado|Denver=America/Denver
        Connecticut=America/New_York
        Delaware=America/New_York
        Florida|Miami|Orlando|Tampa=America/New_York
        Georgia|Atlanta=America/New_York
        Hawaii|Honolulu=Pacific/Honolulu
        Idaho|Boise=America/Boise
        Illinois|Chicago=America/Chicago
        Indiana|Indianapolis=America/Indiana/Indianapolis
        Iowa=America/Chicago
        Kansas=America/Chicago
        Kentucky|Louisville=America/New_York
        Louisiana|New Orleans=America/Chicago
        Maine=America/New_York
        Maryland|Baltimore=America/New_York
        Massachusetts|Boston=America/New_York
        Michigan|Detroit=America/Detroit
        Minnesota|Minneapolis|Saint Paul|St Paul=America/Chicago
        Mississippi=America/Chicago
        Missouri|St Louis|Kansas City=America/Chicago
        Montana=America/Denver
        Nebraska=America/Chicago
        Nevada|Las Vegas=America/Los_Angeles
        New Hampshire=America/New_York
        New Jersey=America/New_York
        New Mexico|Albuquerque=America/Denver
        New York|New York City|NYC=America/New_York
        North Carolina|Charlotte=America/New_York
        North Dakota=America/Chicago
        Ohio|Columbus|Cleveland=America/New_York
        Oklahoma=America/Chicago
        Oregon|Portland=America/Los_Angeles
        Pennsylvania|Philadelphia|Pittsburgh=America/New_York
        Rhode Island=America/New_York
        South Carolina=America/New_York
        South Dakota=America/Chicago
        Tennessee|Nashville|Memphis=America/Chicago
        Texas|Houston|Dallas|Austin|San Antonio=America/Chicago
        Utah|Salt Lake City=America/Denver
        Vermont=America/New_York
        Virginia=America/New_York
        Washington|Seattle=America/Los_Angeles
        Washington DC|DC=America/New_York
        West Virginia=America/New_York
        Wisconsin|Milwaukee=America/Chicago
        Wyoming=America/Denver
        Puerto Rico=America/Puerto_Rico
        USA|United States|America|US=America/New_York
        Ontario|Toronto|Ottawa=America/Toronto
        Quebec|Montreal=America/Toronto
        British Columbia|Vancouver=America/Vancouver
        Alberta|Calgary|Edmonton=America/Edmonton
        Manitoba|Winnipeg=America/Winnipeg
        Saskatchewan|Regina=America/Regina
        Nova Scotia|Halifax|New Brunswick|Prince Edward Island=America/Halifax
        Newfoundland|St Johns=America/St_Johns
        Canada=America/Toronto
        Mexico|Mexico City=America/Mexico_City
        Cancun=America/Cancun
        Tijuana=America/Tijuana
        Brazil|Brasil|Sao Paulo|Rio de Janeiro|Brasilia=America/Sao_Paulo
        Argentina|Buenos Aires=America/Argentina/Buenos_Aires
        Chile|Santiago=America/Santiago
        Colombia|Bogota=America/Bogota
        Peru|Lima=America/Lima
        Venezuela|Caracas=America/Caracas
        Cuba|Havana=America/Havana
        Jamaica=America/Jamaica
        Ecuador|Quito=America/Guayaquil
        Bolivia|La Paz=America/La_Paz
        Paraguay|Asuncion=America/Asuncion
        Uruguay|Montevideo=America/Montevideo
        Costa Rica=America/Costa_Rica
        Panama=America/Panama
        Guatemala=America/Guatemala
        Dominican Republic|Santo Domingo=America/Santo_Domingo
        United Kingdom|UK|England|Scotland|Wales|Great Britain|London|Manchester|Edinburgh=Europe/London
        Ireland|Dublin=Europe/Dublin
        Portugal|Lisbon|Lissabon=Europe/Lisbon
        Spain|Spanien|Madrid|Barcelona=Europe/Madrid
        Canary Islands|Tenerife|Gran Canaria=Atlantic/Canary
        France|Frankreich|Paris=Europe/Paris
        Germany|Deutschland|Berlin|Munich|Muenchen|Munchen|Hamburg|Frankfurt|Cologne|Koeln|Koln|Stuttgart=Europe/Berlin
        Austria|Oesterreich|Osterreich|Vienna|Wien=Europe/Vienna
        Switzerland|Schweiz|Zurich|Zuerich|Geneva|Bern=Europe/Zurich
        Italy|Italien|Rome|Rom|Milan=Europe/Rome
        Netherlands|Holland|Niederlande|Amsterdam=Europe/Amsterdam
        Belgium|Brussels=Europe/Brussels
        Luxembourg=Europe/Luxembourg
        Denmark|Copenhagen=Europe/Copenhagen
        Norway|Oslo=Europe/Oslo
        Sweden|Stockholm=Europe/Stockholm
        Finland|Helsinki=Europe/Helsinki
        Iceland|Reykjavik=Atlantic/Reykjavik
        Poland|Polen|Warsaw=Europe/Warsaw
        Czechia|Czech Republic|Tschechien|Prague=Europe/Prague
        Slovakia|Bratislava=Europe/Bratislava
        Hungary|Budapest=Europe/Budapest
        Slovenia|Ljubljana=Europe/Ljubljana
        Croatia|Kroatien|Zagreb=Europe/Zagreb
        Serbia|Belgrade=Europe/Belgrade
        Bosnia|Sarajevo=Europe/Sarajevo
        Albania|Tirana=Europe/Tirane
        Greece|Griechenland|Athens=Europe/Athens
        Bulgaria|Sofia=Europe/Sofia
        Romania|Bucharest=Europe/Bucharest
        Ukraine|Kyiv|Kiev=Europe/Kiev
        Belarus|Minsk=Europe/Minsk
        Lithuania|Vilnius=Europe/Vilnius
        Latvia|Riga=Europe/Riga
        Estonia|Tallinn=Europe/Tallinn
        Russia|Russland|Moscow|Moskau|Saint Petersburg=Europe/Moscow
        Turkey|Turkiye|Tuerkei|Turkei|Istanbul|Ankara=Europe/Istanbul
        Cyprus|Nicosia=Asia/Nicosia
        Malta=Europe/Malta
        Japan|Tokyo|Osaka|Kyoto=Asia/Tokyo
        South Korea|Korea|Seoul=Asia/Seoul
        North Korea|Pyongyang=Asia/Pyongyang
        China|Beijing|Shanghai|Shenzhen=Asia/Shanghai
        Hong Kong=Asia/Hong_Kong
        Taiwan|Taipei=Asia/Taipei
        Philippines|Manila=Asia/Manila
        Vietnam|Hanoi|Ho Chi Minh City|Saigon=Asia/Ho_Chi_Minh
        Thailand|Bangkok=Asia/Bangkok
        Malaysia|Kuala Lumpur=Asia/Kuala_Lumpur
        Singapore=Asia/Singapore
        Indonesia|Jakarta=Asia/Jakarta
        Bali=Asia/Makassar
        India|Indien|Delhi|New Delhi|Mumbai|Bangalore|Kolkata=Asia/Kolkata
        Pakistan|Karachi|Islamabad=Asia/Karachi
        Bangladesh|Dhaka=Asia/Dhaka
        Sri Lanka|Colombo=Asia/Colombo
        Nepal|Kathmandu=Asia/Kathmandu
        Afghanistan|Kabul=Asia/Kabul
        Iran|Tehran=Asia/Tehran
        Iraq|Baghdad=Asia/Baghdad
        Israel|Jerusalem|Tel Aviv=Asia/Jerusalem
        Jordan|Amman=Asia/Amman
        Lebanon|Beirut=Asia/Beirut
        Syria|Damascus=Asia/Damascus
        Saudi Arabia|Riyadh|Mecca=Asia/Riyadh
        United Arab Emirates|UAE|Dubai|Abu Dhabi=Asia/Dubai
        Qatar|Doha=Asia/Qatar
        Kuwait=Asia/Kuwait
        Oman|Muscat=Asia/Muscat
        Kazakhstan|Almaty=Asia/Almaty
        Uzbekistan|Tashkent=Asia/Tashkent
        Tbilisi|Georgia country=Asia/Tbilisi
        Armenia|Yerevan=Asia/Yerevan
        Azerbaijan|Baku=Asia/Baku
        Mongolia|Ulaanbaatar=Asia/Ulaanbaatar
        Myanmar|Yangon=Asia/Yangon
        Australia|New South Wales|NSW|Sydney|Canberra=Australia/Sydney
        Victoria|Melbourne=Australia/Melbourne
        Queensland|Brisbane=Australia/Brisbane
        South Australia|Adelaide=Australia/Adelaide
        Western Australia|Perth=Australia/Perth
        Tasmania|Hobart=Australia/Hobart
        Northern Territory|Darwin=Australia/Darwin
        New Zealand|Auckland|Wellington=Pacific/Auckland
        Fiji=Pacific/Fiji
        Egypt|Cairo=Africa/Cairo
        South Africa|Johannesburg|Cape Town=Africa/Johannesburg
        Nigeria|Lagos|Abuja=Africa/Lagos
        Kenya|Nairobi=Africa/Nairobi
        Morocco|Casablanca=Africa/Casablanca
        Algeria|Algiers=Africa/Algiers
        Tunisia|Tunis=Africa/Tunis
        Ethiopia|Addis Ababa=Africa/Addis_Ababa
        Ghana|Accra=Africa/Accra
        Senegal|Dakar=Africa/Dakar
        Tanzania|Dar es Salaam=Africa/Dar_es_Salaam
        Uganda|Kampala=Africa/Kampala
        Libya|Tripoli=Africa/Tripoli
        Sudan|Khartoum=Africa/Khartoum
        Angola|Luanda=Africa/Luanda
        Zimbabwe|Harare=Africa/Harare
        Congo|Kinshasa=Africa/Kinshasa
        UTC|GMT=Etc/UTC
        """;

    /// <summary>Normalised name → (display name, IANA id).</summary>
    private static readonly Dictionary<string, (string Name, string Iana)> Places = Parse();

    /// <summary>Finds the zone for a typed place. Returns the Windows zone id and a nice label, or null.</summary>
    public static (string ZoneId, string Label)? Find(string input)
    {
        string text = input.Trim();
        if (text.Length == 0) return null;

        // An IANA id typed directly ("America/Chicago")
        if (text.Contains('/') && ToWindows(text) is string direct) return (direct, text[(text.LastIndexOf('/') + 1)..].Replace('_', ' '));

        // "Minneapolis, Minnesota, USA" → try every part, most specific first
        foreach (var part in text.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            if (Places.TryGetValue(Normalize(part), out var hit) && ToWindows(hit.Iana) is string id)
                return (id, part.Any(c => c > 127) ? part : hit.Name); // keep "München" as typed

        string q = Normalize(text);
        if (q.Length >= 3)
        {
            // Started typing a known place ("Minnes")
            foreach (var (key, hit) in Places)
                if (key.StartsWith(q) && ToWindows(hit.Iana) is string id) return (id, hit.Name);

            // Cities Windows names itself, e.g. "(UTC+09:00) Osaka, Sapporo, Tokyo"
            var zone = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z =>
                Normalize(z.DisplayName).Contains(q) || Normalize(z.Id).Contains(q));
            if (zone != null) return (zone.Id, text);
        }
        return null;
    }

    /// <summary>Every place in the table that doesn't map to a Windows zone on this PC (should be empty).</summary>
    public static List<string> Unresolved() =>
        Places.Values.Where(p => ToWindows(p.Iana) == null).Select(p => $"{p.Name} → {p.Iana}").Distinct().ToList();

    private static string? ToWindows(string iana)
    {
        if (iana == "Etc/UTC") return "UTC";
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var win)) return win;
        return TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var tz) ? tz.Id : null;
    }

    private static Dictionary<string, (string, string)> Parse()
    {
        var map = new Dictionary<string, (string, string)>();
        foreach (var line in Table.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (names, iana) = (line[..line.IndexOf('=')].Split('|'), line[(line.IndexOf('=') + 1)..]);
            foreach (var name in names) map.TryAdd(Normalize(name), (name, iana));
        }
        return map;
    }

    /// <summary>Lower case, no accents/umlauts ("Zürich" = "zurich" = "zuerich" via the table), single spaces, no dots.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '.' or '\'') continue;
            sb.Append(char.IsWhiteSpace(c) || c is '-' or '_' ? ' ' : char.ToLowerInvariant(c));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
