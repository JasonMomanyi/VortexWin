using Microsoft.Extensions.Logging;

namespace VortexWin.Service.Engine;

/// <summary>
/// Countdown timer engine with threshold events at 75%, 50%, 25%, and 0% (expired).
/// Thread-safe. State held in memory only — not persisted to disk.
/// </summary>
public sealed class TimerEngine : IDisposable
{
    private readonly ILogger<TimerEngine> _logger;
    private System.Timers.Timer? _timer;
    private int _totalSeconds;
    private int _remainingSeconds;
    private readonly object _lock = new();
    private bool _paused;

    // Threshold tracking — only fire each once
    private bool _fired75;
    private bool _fired50;
    private bool _fired25;

    public event EventHandler<ThresholdEventArgs>? ThresholdReached;
    public event EventHandler? Expired;

    public int RemainingSeconds
    {
        get { lock (_lock) { return _remainingSeconds; } }
    }

    public int TotalSeconds
    {
        get { lock (_lock) { return _totalSeconds; } }
    }

    public bool IsRunning
    {
        get { lock (_lock) { return _timer?.Enabled == true && !_paused; } }
    }

    public bool IsPaused
    {
        get { lock (_lock) { return _paused; } }
    }

    public TimerEngine(ILogger<TimerEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start the countdown timer with the specified duration.
    /// </summary>
    public void Start(int durationSeconds)
    {
        lock (_lock)
        {
            Stop();

            _totalSeconds = durationSeconds;
            _remainingSeconds = durationSeconds;
            _paused = false;
            _fired75 = false;
            _fired50 = false;
            _fired25 = false;

            _timer = new System.Timers.Timer(1000); // 1-second tick
            _timer.Elapsed += OnTick;
            _timer.AutoReset = true;
            _timer.Start();

            _logger.LogInformation("Timer started: {Duration}s", durationSeconds);
        }
    }

    /// <summary>
    /// Stop and cancel the timer.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_timer is not null)
            {
                _timer.Stop();
                _timer.Elapsed -= OnTick;
                _timer.Dispose();
                _timer = null;
            }
            _paused = false;
            _logger.LogInformation("Timer stopped.");
        }
    }

    /// <summary>
    /// Pause the timer without resetting remaining time.
    /// </summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_timer is not null && !_paused)
            {
                _timer.Stop();
                _paused = true;
                _logger.LogInformation("Timer paused at {Remaining}s", _remainingSeconds);
            }
        }
    }

    /// <summary>
    /// Resume the timer from paused state.
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_timer is not null && _paused)
            {
                _timer.Start();
                _paused = false;
                _logger.LogInformation("Timer resumed at {Remaining}s", _remainingSeconds);
            }
        }
    }

    /// <summary>
    /// Halve the remaining time (penalty timer feature).
    /// </summary>
    public void HalveRemainingTime()
    {
        lock (_lock)
        {
            _remainingSeconds = Math.Max(1, _remainingSeconds / 2);
            _logger.LogWarning("Penalty applied! Remaining time halved to {Remaining}s", _remainingSeconds);
        }
    }

    private void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        int remaining;
        int total;

        lock (_lock)
        {
            if (_paused) return;

            _remainingSeconds--;
            remaining = _remainingSeconds;
            total = _totalSeconds;
        }

        if (total <= 0) return;

        double percentRemaining = (double)remaining / total * 100;

        // Check thresholds
        if (percentRemaining <= 75 && !_fired75)
        {
            _fired75 = true;
            ThresholdReached?.Invoke(this, new ThresholdEventArgs(75, remaining));
        }
        if (percentRemaining <= 50 && !_fired50)
        {
            _fired50 = true;
            ThresholdReached?.Invoke(this, new ThresholdEventArgs(50, remaining));
        }
        if (percentRemaining <= 25 && !_fired25)
        {
            _fired25 = true;
            ThresholdReached?.Invoke(this, new ThresholdEventArgs(25, remaining));
        }

        // Timer expired
        if (remaining <= 0)
        {
            Stop();
            _logger.LogCritical("Timer expired!");
            Expired?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Event args for timer threshold events.
/// </summary>
public sealed class ThresholdEventArgs : EventArgs
{
    public int PercentRemaining { get; }
    public int SecondsRemaining { get; }

    public ThresholdEventArgs(int percentRemaining, int secondsRemaining)
    {
        PercentRemaining = percentRemaining;
        SecondsRemaining = secondsRemaining;
    }
}
