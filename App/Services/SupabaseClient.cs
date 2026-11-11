using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Juniro.App.Services;

/// <summary>Levée quand la session a expiré ou n'existe pas : l'application revient à la connexion.</summary>
public sealed class NotSignedInException : Exception
{
    public NotSignedInException(string message) : base(message) { }
}

/// <summary>
/// Accès direct à Supabase par HTTP (sans bibliothèque tierce) :
///   - connexion : /auth/v1/token (e-mail + mot de passe, puis renouvellement du jeton) ;
///   - données   : /rest/v1/&lt;table ou vue&gt;?filtres (PostgREST) et /rest/v1/rpc/&lt;fonction&gt;.
/// Chaque requête porte la clé publique (apikey) et le jeton de l'utilisateur : la base
/// n'autorise que ce que ses règles de sécurité (RLS) permettent à ce compte.
/// Syntaxe des filtres : https://postgrest.org (ex. league=eq.LaLiga&amp;order=kickoff_utc.desc).
/// </summary>
public sealed class SupabaseClient
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http = new() { BaseAddress = new Uri(AppConfig.SupabaseUrl), Timeout = TimeSpan.FromSeconds(30) };
    private readonly SessionStore _store;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AuthSession? _session;

    public SupabaseClient(SessionStore store) => _store = store;

    /// <summary>Identifiant de l'utilisateur connecté (null si déconnecté).</summary>
    public Guid? UserId => _session?.User?.Id;

    /// <summary>E-mail de l'utilisateur connecté.</summary>
    public string? Email => _session?.User?.Email;

    public bool IsSignedIn => _session != null;

    // ------------------------------------------------------------------ connexion

    /// <summary>Connexion par e-mail et mot de passe ; la session est mémorisée sur l'appareil.</summary>
    public async Task SignInAsync(string email, string password)
    {
        var body = JsonSerializer.Serialize(new { email, password });
        using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/token?grant_type=password")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);
        using var res = await _http.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(res.StatusCode == HttpStatusCode.BadRequest
                ? "E-mail ou mot de passe incorrect."
                : $"Connexion impossible ({(int)res.StatusCode}).");
        await SetSessionAsync(text);
    }

    /// <summary>Reprend la session mémorisée (et la renouvelle si besoin). false = il faut se connecter.</summary>
    public async Task<bool> RestoreSessionAsync()
    {
        var json = await _store.LoadAsync();
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            _session = JsonSerializer.Deserialize<AuthSession>(json, Json);
            if (_session?.RefreshToken == null) return false;
            await EnsureFreshTokenAsync(force: _session.IsExpiringSoon);
            return true;
        }
        catch
        {
            _session = null;
            _store.Clear();
            return false;
        }
    }

    /// <summary>Déconnexion : oublie la session sur l'appareil (et la ferme côté serveur si possible).</summary>
    public async Task SignOutAsync()
    {
        try
        {
            if (_session != null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/logout");
                AddHeaders(req);
                await _http.SendAsync(req);
            }
        }
        catch
        {
            // hors ligne : on oublie la session localement quand même
        }
        _session = null;
        _store.Clear();
    }

    private async Task SetSessionAsync(string json)
    {
        var s = JsonSerializer.Deserialize<AuthSession>(json, Json)
                ?? throw new InvalidOperationException("Réponse de connexion illisible.");
        if (s.ExpiresAt == 0 && s.ExpiresIn > 0)
            s.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + s.ExpiresIn;
        if (s.User == null && _session?.User != null)
            s.User = _session.User;
        _session = s;
        await _store.SaveAsync(JsonSerializer.Serialize(s, Json));
    }

    /// <summary>Renouvelle le jeton d'accès s'il expire dans moins d'une minute.</summary>
    private async Task EnsureFreshTokenAsync(bool force = false)
    {
        if (_session == null) throw new NotSignedInException("Non connecté.");
        if (!force && !_session.IsExpiringSoon) return;
        await _refreshLock.WaitAsync();
        try
        {
            if (!force && !_session.IsExpiringSoon) return;
            var body = JsonSerializer.Serialize(new { refresh_token = _session.RefreshToken });
            using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);
            using var res = await _http.SendAsync(req);
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _session = null;
                _store.Clear();
                throw new NotSignedInException("Session expirée : reconnectez-vous.");
            }
            await SetSessionAsync(text);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);
        if (_session?.AccessToken != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
    }

    // ------------------------------------------------------------------ données

    /// <summary>GET /rest/v1/{resource}?{query} -> liste d'objets.</summary>
    public async Task<List<T>> GetAsync<T>(string resource, string query = "select=*")
    {
        string text = await SendAsync(HttpMethod.Get, $"/rest/v1/{resource}?{query}", null);
        return JsonSerializer.Deserialize<List<T>>(text, Json) ?? new List<T>();
    }

    /// <summary>POST /rest/v1/rpc/{function} -> liste d'objets (fonction qui renvoie une table).</summary>
    public async Task<List<T>> RpcAsync<T>(string function, object args)
    {
        string text = await SendAsync(HttpMethod.Post, $"/rest/v1/rpc/{function}", JsonSerializer.Serialize(args, Json));
        return JsonSerializer.Deserialize<List<T>>(text, Json) ?? new List<T>();
    }

    /// <summary>INSERT : POST /rest/v1/{table}.</summary>
    public Task InsertAsync(string table, object row) =>
        SendAsync(HttpMethod.Post, $"/rest/v1/{table}", JsonSerializer.Serialize(row, Json));

    /// <summary>UPDATE : PATCH /rest/v1/{table}?{filter}.</summary>
    public Task UpdateAsync(string table, string filter, object values) =>
        SendAsync(HttpMethod.Patch, $"/rest/v1/{table}?{filter}", JsonSerializer.Serialize(values, Json));

    /// <summary>DELETE : DELETE /rest/v1/{table}?{filter}.</summary>
    public Task DeleteAsync(string table, string filter) =>
        SendAsync(HttpMethod.Delete, $"/rest/v1/{table}?{filter}", null);

    private async Task<string> SendAsync(HttpMethod method, string path, string? body)
    {
        await EnsureFreshTokenAsync();
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, path);
            AddHeaders(req);
            if (body != null)
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                req.Headers.Add("Prefer", "return=minimal");
            }
            using var res = await _http.SendAsync(req);
            string text = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode) return text;

            // Jeton refusé : un renouvellement, puis on réessaie une fois
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                await EnsureFreshTokenAsync(force: true);
                continue;
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new NotSignedInException("Session expirée : reconnectez-vous.");
            throw new InvalidOperationException(ErrorText(res.StatusCode, text));
        }
    }

    /// <summary>Message d'erreur lisible à partir de la réponse PostgREST.</summary>
    private static string ErrorText(HttpStatusCode code, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return $"Erreur Supabase ({(int)code}) : {msg.GetString()}";
        }
        catch
        {
            // pas du JSON
        }
        return $"Erreur Supabase ({(int)code}).";
    }

    // ------------------------------------------------------------------ utilitaires de requête

    /// <summary>Valeur prête pour une URL PostgREST (accents, espaces, signes +).</summary>
    public static string Esc(string value) => Uri.EscapeDataString(value);

    /// <summary>Date au format attendu par PostgREST, en UTC.</summary>
    public static string Iso(DateTimeOffset d) => Esc(d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
}

/// <summary>Session renvoyée par /auth/v1/token.</summary>
public sealed class AuthSession
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public long ExpiresIn { get; set; }
    public long ExpiresAt { get; set; }
    public AuthUser? User { get; set; }

    [JsonIgnore]
    public bool IsExpiringSoon => ExpiresAt - 60 < DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public sealed class AuthUser
{
    public Guid Id { get; set; }
    public string? Email { get; set; }
}
