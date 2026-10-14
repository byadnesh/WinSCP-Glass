using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinScpGlass.Models;
using WinScpGlass.Services;
using WinSCP;

namespace WinScpGlass.ViewModels;

public enum TransferDirection { Upload, Download }
public enum TransferState { Queued, Running, Done, Failed, Canceled }

public sealed partial class TransferJob : ObservableObject
{
    public required TransferDirection Direction { get; init; }
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetDirectory { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>Runs on the UI thread after a successful transfer (e.g. open the downloaded file).</summary>
    public Func<Task>? AfterComplete { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsActive), nameof(IsFinished), nameof(CanRetry))]
    private TransferState _state;
    [ObservableProperty] private double _progress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _speedText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    internal volatile bool CancelRequested;

    public string Glyph => Direction == TransferDirection.Upload ? "" : "";
    public string RouteText => $"{(Direction == TransferDirection.Upload ? "Upload to" : "Download to")} {TargetDirectory}";
    public bool IsActive => State is TransferState.Queued or TransferState.Running;
    public bool IsFinished => !IsActive;
    public bool CanRetry => State is TransferState.Failed or TransferState.Canceled;

    public string StatusText => State switch
    {
        TransferState.Queued => "Waiting",
        TransferState.Running => string.IsNullOrEmpty(SpeedText) ? $"{Progress:0}%" : $"{Progress:0}% · {SpeedText}",
        TransferState.Done => "Done",
        TransferState.Canceled => "Canceled",
        _ => Error ?? "Failed"
    };
}

/// <summary>Runs transfers one after another over the session connection.</summary>
public sealed partial class TransferQueueViewModel : ObservableObject
{
    private readonly SessionViewModel _session;
    private bool _running;
    private int _completedInBatch;

    public ObservableCollection<TransferJob> Jobs { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _overallProgress;

    public TransferQueueViewModel(SessionViewModel session)
    {
        _session = session;
    }

    public bool HasActiveJobs => Jobs.Any(j => j.IsActive);

    public void Add(TransferJob job)
    {
        Jobs.Insert(0, job);
        IsExpanded = true;
        UpdateSummary();
        if (!_running) _ = ProcessAsync();
    }

    private async Task ProcessAsync()
    {
        _running = true;
        IsBusy = true;
        try
        {
            while (Jobs.LastOrDefault(j => j.State == TransferState.Queued) is { } job)
            {
                await RunAsync(job);
                UpdateSummary();
            }
        }
        finally
        {
            _running = false;
            IsBusy = false;
            UpdateSummary();
            if (_completedInBatch > 0)
            {
                App.Shell.Notify(_completedInBatch == 1 ? "Transfer complete" : $"{_completedInBatch} transfers complete", Tone.Success);
            }
            _completedInBatch = 0;
        }
    }

    private async Task RunAsync(TransferJob job)
    {
        job.State = TransferState.Running;
        job.Progress = 0;
        job.Error = null;
        UpdateSummary();

        var dispatcher = Application.Current.Dispatcher;
        long lastTick = 0;
        void OnProgress(FileTransferProgressEventArgs e)
        {
            if (job.CancelRequested) e.Cancel = true;
            long now = Environment.TickCount64;
            if (now - lastTick < 120) return;
            lastTick = now;
            double percent = e.OverallProgress * 100;
            string speed = Format.Speed(e.CPS);
            dispatcher.BeginInvoke(() =>
            {
                job.Progress = percent;
                job.SpeedText = speed;
                UpdateSummary();
            });
        }

        try
        {
            var connection = _session.Connection;
            if (job.Direction == TransferDirection.Upload)
                await connection.UploadAsync(job.SourcePath, job.TargetDirectory, OnProgress);
            else
                await connection.DownloadAsync(job.SourcePath, job.TargetDirectory, OnProgress);

            if (job.CancelRequested)
            {
                job.State = TransferState.Canceled;
                return;
            }
            job.Progress = 100;
            job.SpeedText = "";
            job.State = TransferState.Done;
            _completedInBatch++;
            _session.OnTransferCompleted(job);
            if (job.AfterComplete != null) await job.AfterComplete();
        }
        catch (Exception) when (job.CancelRequested)
        {
            job.State = TransferState.Canceled;
        }
        catch (ConnectionLostException ex)
        {
            job.Error = "Connection lost";
            job.State = TransferState.Failed;
            await _session.HandleConnectionLostAsync(ex);
        }
        catch (Exception ex)
        {
            job.Error = FirstLine(ex.Message);
            job.State = TransferState.Failed;
            App.Shell.Notify($"“{job.Name}” failed: {job.Error}", Tone.Danger);
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? text;
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private void UpdateSummary()
    {
        HasJobs = Jobs.Count > 0;
        int active = Jobs.Count(j => j.IsActive);
        var running = Jobs.FirstOrDefault(j => j.State == TransferState.Running);
        Summary = active == 0
            ? $"{Jobs.Count(j => j.State == TransferState.Done)} completed"
            : running != null && !string.IsNullOrEmpty(running.SpeedText)
                ? $"{active} remaining · {running.SpeedText}"
                : $"{active} remaining";
        var batch = Jobs.Where(j => j.State != TransferState.Canceled).ToList();
        OverallProgress = active == 0 || batch.Count == 0 ? 0 : batch.Average(j => j.State == TransferState.Done ? 100 : j.Progress);
        OnPropertyChanged(nameof(HasActiveJobs));
    }

    [RelayCommand]
    private void Cancel(TransferJob? job)
    {
        if (job == null) return;
        if (job.State == TransferState.Queued) job.State = TransferState.Canceled;
        else if (job.State == TransferState.Running) job.CancelRequested = true;
        UpdateSummary();
    }

    [RelayCommand]
    private void CancelAll()
    {
        foreach (var job in Jobs.Where(j => j.IsActive).ToList()) Cancel(job);
    }

    [RelayCommand]
    private void Retry(TransferJob? job)
    {
        if (job == null || !job.CanRetry) return;
        job.CancelRequested = false;
        job.Error = null;
        job.Progress = 0;
        job.State = TransferState.Queued;
        UpdateSummary();
        if (!_running) _ = ProcessAsync();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished).ToList()) Jobs.Remove(job);
        UpdateSummary();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
