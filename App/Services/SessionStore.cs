namespace Juniro.App.Services;

/// <summary>
/// Garde la session (jetons de connexion) sur l'appareil pour ne pas redemander le mot de
/// passe à chaque ouverture. Stockage sécurisé du système (SecureStorage) ; s'il n'est pas
/// disponible (application Windows non empaquetée), repli sur les préférences de l'application.
/// </summary>
public sealed class SessionStore
{
    private const string Key = "juniro_session";

    public async Task SaveAsync(string json)
    {
        try
        {
            await SecureStorage.Default.SetAsync(Key, json);
            return;
        }
        catch
        {
            // SecureStorage indisponible : repli ci-dessous
        }
        try { Preferences.Default.Set(Key, json); } catch { /* pas de mémorisation possible */ }
    }

    public async Task<string?> LoadAsync()
    {
        try
        {
            var v = await SecureStorage.Default.GetAsync(Key);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch
        {
            // repli ci-dessous
        }
        try
        {
            var v = Preferences.Default.Get(Key, "");
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        try { SecureStorage.Default.Remove(Key); } catch { }
        try { Preferences.Default.Remove(Key); } catch { }
    }
}
