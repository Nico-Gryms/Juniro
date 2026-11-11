namespace Juniro.App;

/// <summary>
/// Adresse du projet Supabase et clé PUBLIQUE (« anon »). Cette clé peut figurer dans
/// l'application : elle ne donne accès qu'à ce que les règles de sécurité (RLS) autorisent
/// à un compte connecté. Ne JAMAIS mettre ici la clé « service_role » ni un mot de passe.
/// </summary>
public static class AppConfig
{
    public const string SupabaseUrl = "https://ysqdklhsjfzbgwazgfkf.supabase.co";

    public const string SupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InlzcWRrbGhzamZ6Ymd3YXpnZmtmIiwicm9sZSI6ImFub24iLCJpYXQiOjE3OTQxNTI1MDksImV4cCI6MjEwOTcyODUwOX0.rspencOLWbxOWknr4BsVECos7HYgER5MbVYDdmJOELc";

    /// <summary>Seuils des verdicts (même règle que les vues Supabase) : ROI de test en %.</summary>
    public const double WinningRoi = 3.0;
    public const double LosingRoi = -3.0;
}
