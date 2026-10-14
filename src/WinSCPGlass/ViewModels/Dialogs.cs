using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinScpGlass.ViewModels;

public enum Tone { Info, Success, Warning, Danger, Security }

public sealed record DialogButton(string Id, string Text, bool IsPrimary = false, bool IsDanger = false, bool IsCancel = false);

/// <summary>A floating glass sheet. The main window renders it over a blurred backdrop.</summary>
public sealed partial class DialogRequest : ObservableObject
{
    public required string Title { get; init; }
    public string Message { get; init; } = "";
    public string? Detail { get; init; }
    public string Glyph { get; init; } = "";
    public Tone Tone { get; init; } = Tone.Info;
    public IReadOnlyList<DialogButton> Buttons { get; init; } = new[] { new DialogButton("ok", "OK", IsPrimary: true) };
    public bool HasInput { get; init; }
    public bool IsPassword { get; init; }
    public string? InputPlaceholder { get; init; }

    [ObservableProperty]
    private string _inputText = "";

    internal TaskCompletionSource<string?> Completion { get; } = new();

    [RelayCommand]
    private void Choose(DialogButton? button) => Completion.TrySetResult(button?.Id);

    public void Cancel()
    {
        var cancel = Buttons.FirstOrDefault(b => b.IsCancel);
        Completion.TrySetResult(cancel?.Id);
    }

    public void Accept()
    {
        var primary = Buttons.FirstOrDefault(b => b.IsPrimary) ?? Buttons.LastOrDefault();
        Completion.TrySetResult(primary?.Id);
    }
}

/// <summary>App-wide dialogs; shown one at a time.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static MainViewModel? Host { get; set; }

    public static async Task<string?> ShowAsync(DialogRequest request)
    {
        if (Host == null) return null;
        await Gate.WaitAsync();
        try
        {
            Host.ActiveDialog = request;
            return await request.Completion.Task;
        }
        finally
        {
            Host.ActiveDialog = null;
            Gate.Release();
        }
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false, string glyph = "")
    {
        var result = await ShowAsync(new DialogRequest
        {
            Title = title,
            Message = message,
            Glyph = glyph,
            Tone = danger ? Tone.Danger : Tone.Info,
            Buttons = new[]
            {
                new DialogButton("cancel", "Cancel", IsCancel: true),
                new DialogButton("ok", confirmText, IsPrimary: !danger, IsDanger: danger),
            }
        });
        return result == "ok";
    }

    public static Task ErrorAsync(string title, string message, string? detail = null) => ShowAsync(new DialogRequest
    {
        Title = title,
        Message = message,
        Detail = detail,
        Glyph = "",
        Tone = Tone.Danger,
        Buttons = new[] { new DialogButton("ok", "OK", IsPrimary: true, IsCancel: true) }
    });

    public static async Task<string?> PromptAsync(string title, string message, string confirmText,
        string initial = "", string? placeholder = null, bool isPassword = false, string glyph = "")
    {
        var request = new DialogRequest
        {
            Title = title,
            Message = message,
            Glyph = glyph,
            HasInput = true,
            IsPassword = isPassword,
            InputPlaceholder = placeholder,
            InputText = initial,
            Buttons = new[]
            {
                new DialogButton("cancel", "Cancel", IsCancel: true),
                new DialogButton("ok", confirmText, IsPrimary: true),
            }
        };
        var result = await ShowAsync(request);
        return result == "ok" ? request.InputText : null;
    }
}

public sealed record Toast(string Message, string Glyph, Tone Tone);
