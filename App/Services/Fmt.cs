using System.Globalization;

namespace Juniro.App.Services;

/// <summary>Mise en forme des nombres et des dates, à la française.</summary>
public static class Fmt
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Probabilité 0..1 -> « 45 % » (« – » si absente).</summary>
    public static string Pct(double? p, int digits = 0) =>
        p is null ? "–" : (100 * p.Value).ToString("F" + digits, Fr) + " %";

    /// <summary>Pourcentage déjà en % -> « 45,2 % ».</summary>
    public static string Pct100(double? v, int digits = 1) =>
        v is null ? "–" : v.Value.ToString("F" + digits, Fr) + " %";

    /// <summary>Nombre signé : « +4,2 % », « −1,3 u ».</summary>
    public static string Signed(double? v, int digits = 1, string suffix = "") =>
        v is null ? "–" : (v.Value >= 0 ? "+" : "−") + Math.Abs(v.Value).ToString("F" + digits, Fr) + suffix;

    /// <summary>Cote : « 1,85 ».</summary>
    public static string Odd(double? v) => v is null ? "–" : v.Value.ToString("F2", Fr);

    /// <summary>Nombre simple : « 1,6 ».</summary>
    public static string Num(double? v, int digits = 1) => v is null ? "–" : v.Value.ToString("F" + digits, Fr);

    /// <summary>Heure locale du téléphone : « sam. 08/11 · 18:30 ».</summary>
    public static string Kickoff(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        return local.ToString("ddd dd/MM · HH:mm", Fr);
    }

    /// <summary>Titre de journée : « Aujourd'hui », « Demain », « samedi 15 novembre ».</summary>
    public static string DayTitle(DateTime localDate)
    {
        var today = DateTime.Today;
        if (localDate.Date == today) return "Aujourd'hui";
        if (localDate.Date == today.AddDays(1)) return "Demain";
        if (localDate.Date == today.AddDays(-1)) return "Hier";
        var s = localDate.ToString("dddd d MMMM", Fr);
        return char.ToUpper(s[0], Fr) + s[1..];
    }

    /// <summary>Date longue : « lundi 9 novembre ».</summary>
    public static string DayLong(DateTime localDate)
    {
        var s = localDate.ToString("dddd d MMMM", Fr);
        return char.ToUpper(s[0], Fr) + s[1..];
    }

    // Préfixes ignorés pour les initiales d'une équipe (« RC Lens » -> LEN, « FC Nantes » -> NAN)
    private static readonly HashSet<string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FC", "AS", "RC", "SC", "AC", "OGC", "SV", "VfB", "VfL", "TSG", "CF", "CD", "UD", "SD", "RCD", "AFC",
        "AJ", "LOSC", "OL", "OM", "SCO", "US", "SM", "ESTAC", "FK", "1.", "Real", "Club", "Stade", "Olympique", "Racing"
    };

    /// <summary>Trois lettres pour la pastille d'une équipe : « Lens » -> LEN, « Bayern Munich » -> BAY.</summary>
    public static string Initials(string? team)
    {
        if (string.IsNullOrWhiteSpace(team)) return "?";
        var words = team.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var main = words.FirstOrDefault(w => !Prefixes.Contains(w) && w.Any(char.IsLetter)) ?? words[0];
        var letters = new string(main.Where(char.IsLetter).ToArray());
        if (letters.Length == 0) letters = team;
        return letters[..Math.Min(3, letters.Length)].ToUpperInvariant();
    }

    /// <summary>Lit un nombre saisi avec une virgule ou un point.</summary>
    public static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
