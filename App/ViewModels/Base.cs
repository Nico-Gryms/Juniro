using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Juniro.App.Services;

namespace Juniro.App.ViewModels;

/// <summary>Objet observable : prévient l'écran quand une propriété change.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// Base des ViewModels : indicateur de chargement, message d'erreur, et RunAsync qui
/// exécute une action en attrapant les erreurs (session expirée -> retour à la connexion).
/// </summary>
public abstract class BaseViewModel : ObservableObject
{
    private bool _isBusy;
    private string? _error;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Exécute une action asynchrone : chargement affiché, erreurs transformées en message.</summary>
    protected async Task RunAsync(Func<Task> action, bool showBusy = true)
    {
        if (showBusy && IsBusy) return;
        try
        {
            if (showBusy) IsBusy = true;
            Error = null;
            await action();
        }
        catch (NotSignedInException)
        {
            MainThread.BeginInvokeOnMainThread(App.ShowLogin);
        }
        catch (HttpRequestException)
        {
            Error = "Pas de connexion à Internet (ou Supabase injoignable).";
        }
        catch (TaskCanceledException)
        {
            Error = "Le serveur met trop de temps à répondre. Réessayez.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            if (showBusy) IsBusy = false;
        }
    }

    /// <summary>Navigation vers une page de détail avec des paramètres.</summary>
    protected static Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        parameters == null ? Shell.Current.GoToAsync(route) : Shell.Current.GoToAsync(route, parameters);
}

/// <summary>Commande asynchrone simple (bouton, glisser pour actualiser).</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private bool _running;

    public AsyncCommand(Func<Task> execute) : this(_ => execute()) { }

    public AsyncCommand(Func<object?, Task> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(parameter);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
