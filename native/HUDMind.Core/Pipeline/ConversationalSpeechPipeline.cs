using System.Diagnostics;
using HUDMind.Core.AI;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

public enum SpeechState
{
    Idle = 0,
    WaitingAudioGap = 1,
    Speaking = 2,
    Chaining = 3
}

public sealed record SpeechIntent(
    string Text,
    UrgencyLevel Urgency,
    bool IsDirectUserResponse = false,
    bool InterruptCurrent = false,
    string? TopicTag = null,
    double FreshnessTtlSeconds = 6.0,
    DateTimeOffset CreatedAtUtc = default)
{
    public DateTimeOffset EffectiveCreatedAtUtc =>
        CreatedAtUtc == default ? DateTimeOffset.UtcNow : CreatedAtUtc;
}

public sealed record ConnectorContext(
    string PreviousText,
    SpeechIntent NextIntent,
    TimeSpan GapSincePrevious,
    bool WasQueuedWhileSpeaking,
    bool IsEnglish,
    int RotationIndex);

/// <summary>
/// Strategy contract that transforms queued follow-up utterances into natural conversational continuations.
/// </summary>
public interface IConnectorResolver
{
    string BridgeUtterance(ConnectorContext context);
}

/// <summary>
/// Localized discourse connector resolver producing natural conversational flow
/// (e.g., "y ojo que...", "y aparte...", "and watch out...") without repeating prior clauses or awkward transitions.
/// </summary>
public sealed class NaturalConnectorResolver : IConnectorResolver
{
    private static readonly string[] EsExistingConjunctions =
        ["y ", "e ", "pero ", "aunque ", "aparte ", "ojo ", "cuidado ", "igual ", "entonces "];

    private static readonly string[] EnExistingConjunctions =
        ["and ", "plus ", "but ", "though ", "watch ", "heads up", "so "];

    private static readonly HashSet<string> PreserveUppercaseTokens = new(StringComparer.Ordinal)
    {
        "HP", "MP", "CS", "XP", "C4", "A", "B", "C", "Q", "W", "E", "R", "F8", "F9", "F10", "BKB", "FF", "ULT", "ADS"
    };

    public string BridgeUtterance(ConnectorContext context)
    {
        var clean = CoachContentGuard.SanitizeForSpeech(context.NextIntent.Text);
        if (string.IsNullOrWhiteSpace(clean))
            return string.Empty;

        bool shouldBridge = !context.NextIntent.InterruptCurrent
            && (context.WasQueuedWhileSpeaking
                || (!string.IsNullOrWhiteSpace(context.PreviousText) && context.GapSincePrevious.TotalSeconds <= 4.5));
        if (!shouldBridge)
            return clean;

        var lowered = NormalizeLeadingCase(clean);
        var prefixes = context.IsEnglish ? EnExistingConjunctions : EsExistingConjunctions;
        foreach (var p in prefixes)
        {
            if (lowered.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return lowered;
        }

        string connector = (context.IsEnglish, context.NextIntent.Urgency, Math.Abs(context.RotationIndex) % 2) switch
        {
            (true, UrgencyLevel.High, 0) => "and watch out, ",
            (true, UrgencyLevel.High, _) => "and heads up, ",
            (true, _, 0) => "and also ",
            (true, _, _) => "plus ",
            (false, UrgencyLevel.High, 0) => "y ojo, ",
            (false, UrgencyLevel.High, _) => "y cuidado que ",
            (false, _, 0) => "y aparte ",
            (false, _, _) => "y fijate que "
        };

        return string.Concat(connector, lowered);
    }

    private static string NormalizeLeadingCase(string text)
    {
        if (text.Length < 2 || !char.IsUpper(text[0]))
            return text;

        int end = 0;
        while (end < text.Length && char.IsLetterOrDigit(text[end]))
            end++;

        var firstToken = text[..end];
        if (PreserveUppercaseTokens.Contains(firstToken))
            return text;
        if (firstToken.Length >= 2 && firstToken.All(c => char.IsUpper(c) || char.IsDigit(c)))
            return text;

        return char.ToLowerInvariant(text[0]) + text[1..];
    }
}

/// <summary>
/// Cross-platform bounded priority speech scheduler that prevents skipped follow-up callouts,
/// mid-sentence speech cuts, and loud game-audio collisions while applying natural conversational connectors.
/// </summary>
public sealed class ConversationalSpeechCoordinator : IAsyncDisposable
{
    private const int MaxPendingQueue = 2;

    private readonly IVoiceOutputService _voice;
    private readonly IAudioSceneMonitor _audioMonitor;
    private readonly IConnectorResolver _connectorResolver;
    private readonly Func<bool> _isEn;
    private readonly Func<bool> _canSpeak;
    private readonly Action<string>? _onLog;

    // Never nest _queueLock and _directDispatchLock to keep lock ordering strictly conflict-free.
    private readonly object _queueLock = new();
    private readonly object _directDispatchLock = new();
    private readonly List<SpeechIntent> _queue = new(MaxPendingQueue + 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _workerTask;

    private int _stateRaw;
    private string _lastSpokenText = string.Empty;
    private DateTimeOffset _lastSpokenFinishedAtUtc = DateTimeOffset.MinValue;
    private int _connectorRot;
    private int _interruptGen;
    private volatile bool _disposed;
    private int _disposeOnce;

    public ConversationalSpeechCoordinator(
        IVoiceOutputService voice,
        IAudioSceneMonitor? audioMonitor = null,
        IConnectorResolver? connectorResolver = null,
        Func<bool>? isEn = null,
        Func<bool>? canSpeak = null,
        Action<string>? onLog = null)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _audioMonitor = audioMonitor ?? NullAudioSceneMonitor.Instance;
        _connectorResolver = connectorResolver ?? new NaturalConnectorResolver();
        _isEn = isEn ?? (() => false);
        _canSpeak = canSpeak ?? (() => true);
        _onLog = onLog;
        _workerTask = Task.Run(() => WorkerLoopAsync(_disposeCts.Token));
    }

    public SpeechState State => (SpeechState)Volatile.Read(ref _stateRaw);
    private void SetState(SpeechState next) => Volatile.Write(ref _stateRaw, (int)next);

    public int PendingCount
    {
        get
        {
            lock (_queueLock) return _queue.Count;
        }
    }

    public void Enqueue(SpeechIntent intent)
    {
        if (_disposed || string.IsNullOrWhiteSpace(intent.Text))
            return;

        if (!_canSpeak() && !intent.InterruptCurrent)
            return;

        var now = intent.EffectiveCreatedAtUtc;
        var normalized = intent with { CreatedAtUtc = now };

        if (normalized.InterruptCurrent)
        {
            InterruptAndClear();
            if (!_canSpeak())
                return;
        }

        lock (_queueLock)
        {
            if (_disposed) return;

            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if ((now - _queue[i].EffectiveCreatedAtUtc).TotalSeconds > _queue[i].FreshnessTtlSeconds)
                    _queue.RemoveAt(i);
            }

            if (!string.IsNullOrWhiteSpace(normalized.TopicTag))
            {
                int sameTopicIdx = _queue.FindIndex(x =>
                    string.Equals(x.TopicTag, normalized.TopicTag, StringComparison.OrdinalIgnoreCase));
                if (sameTopicIdx >= 0)
                {
                    _queue[sameTopicIdx] = normalized;
                    TrySignal();
                    return;
                }
            }

            if (_queue.Count >= MaxPendingQueue)
            {
                int evictIdx = FindEvictionCandidateLocked();
                _queue.RemoveAt(evictIdx);
            }

            _queue.Add(normalized);
        }

        TrySignal();
    }

    /// <summary>
    /// Awaits clean non-overlapping dispatch for explicit chat answers so callers observe completion accurately.
    /// Direct user replies answer cleanly without injecting an unrequested transition connector.
    /// </summary>
    public async Task<double> SpeakDirectAndAwaitDispatchAsync(SpeechIntent intent, CancellationToken ct = default)
    {
        if (_disposed || ct.IsCancellationRequested || string.IsNullOrWhiteSpace(intent.Text))
            return 0;

        if (intent.InterruptCurrent)
            InterruptAndClear();

        if (!_canSpeak())
            return 0;

        var clean = CoachContentGuard.SanitizeForSpeech(intent.Text);
        if (string.IsNullOrWhiteSpace(clean))
            return 0;

        int genAtStart = Volatile.Read(ref _interruptGen);
        if (ct.IsCancellationRequested || _disposed || !_canSpeak())
            return 0;

        bool waitedGap = false;
        while (!ct.IsCancellationRequested && !_disposed && Volatile.Read(ref _interruptGen) == genAtStart && _canSpeak())
        {
            if (!_voice.SupportsContinuationQueue && !intent.InterruptCurrent && _voice.IsActivelySpeaking)
            {
                if (!await WaitUntilNotActivelySpeakingAsync(genAtStart, ct).ConfigureAwait(false))
                    return 0;
                waitedGap = false;
                continue;
            }

            if (!waitedGap)
            {
                await WaitForGameAudioGapInternalAsync(_audioMonitor, intent.Urgency, isDirectReply: true, intent.InterruptCurrent, genAtStart, ct).ConfigureAwait(false);
                waitedGap = true;
            }

            lock (_directDispatchLock)
            {
                if (ct.IsCancellationRequested || _disposed || Volatile.Read(ref _interruptGen) != genAtStart || !_canSpeak())
                    return 0;

                if (!_voice.SupportsContinuationQueue && !intent.InterruptCurrent && _voice.IsActivelySpeaking)
                {
                    // Another utterance began playing while waiting -> re-yield outside lock without chopping
                }
                else
                {
                    bool chain = !intent.InterruptCurrent;
                    SetState(SpeechState.Speaking);
                    double ms = _voice.SpeakOrQueue(clean, chainIfSpeaking: chain, interruptCurrent: intent.InterruptCurrent);
                    UpdateSpokenStateLocked(clean, chain);
                    return ms;
                }
            }
        }
        return 0;
    }

    public void InterruptAndClear()
    {
        lock (_queueLock)
        {
            Interlocked.Increment(ref _interruptGen);
            _queue.Clear();
        }
        lock (_directDispatchLock)
        {
            _voice.StopSpeaking();
            _lastSpokenText = string.Empty;
            _lastSpokenFinishedAtUtc = DateTimeOffset.MinValue;
            SetState(SpeechState.Idle);
        }
    }

    public Task<double> WaitForGameAudioGapAsync(
        IAudioSceneMonitor? monitor,
        UrgencyLevel urgency,
        bool isDirectReply,
        CancellationToken ct) =>
        WaitForGameAudioGapInternalAsync(monitor, urgency, isDirectReply, interruptCurrent: false, expectedGen: null, ct);

    private async Task<double> WaitForGameAudioGapInternalAsync(
        IAudioSceneMonitor? monitor,
        UrgencyLevel urgency,
        bool isDirectReply,
        bool interruptCurrent,
        int? expectedGen,
        CancellationToken ct)
    {
        var mon = monitor ?? _audioMonitor;
        bool isUrgent = urgency == UrgencyLevel.High || isDirectReply || interruptCurrent;
        double maxWaitMs = isUrgent ? 200.0 : 620.0;
        double busyThreshold = isUrgent ? 0.72 : 0.42;

        var first = mon.SampleOtherAudio();
        if (first.OtherSessionsPeak01 < busyThreshold)
            return first.OtherSessionsPeak01;

        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested &&
               !_disposed &&
               _canSpeak() &&
               (expectedGen is null || Volatile.Read(ref _interruptGen) == expectedGen.Value) &&
               sw.Elapsed.TotalMilliseconds < maxWaitMs)
        {
            await Task.Delay(55, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested ||
                _disposed ||
                !_canSpeak() ||
                (expectedGen is not null && Volatile.Read(ref _interruptGen) != expectedGen.Value))
            {
                break;
            }
            var snap = mon.SampleOtherAudio();
            if (snap.OtherSessionsPeak01 < busyThreshold)
                return snap.OtherSessionsPeak01;
        }
        return mon.SampleOtherAudio().OtherSessionsPeak01;
    }

    private void UpdateSpokenStateLocked(string spokenText, bool chained)
    {
        _lastSpokenText = spokenText;
        var now = DateTimeOffset.UtcNow;
        var anchor = (chained && _lastSpokenFinishedAtUtc > now) ? _lastSpokenFinishedAtUtc : now;
        double estSec = Math.Clamp(spokenText.Length / 13.5 + 0.35, 0.6, 12.0);
        _lastSpokenFinishedAtUtc = anchor.AddSeconds(estSec);
    }

    private int FindEvictionCandidateLocked()
    {
        int bestIdx = 0;
        for (int i = 1; i < _queue.Count; i++)
        {
            int pCand = PriorityScore(_queue[i]);
            int pBest = PriorityScore(_queue[bestIdx]);
            if (pCand < pBest || (pCand == pBest && _queue[i].EffectiveCreatedAtUtc < _queue[bestIdx].EffectiveCreatedAtUtc))
                bestIdx = i;
        }
        return bestIdx;
    }

    private static int PriorityScore(SpeechIntent item) =>
        (item.IsDirectUserResponse ? 10 : 0) + (int)item.Urgency;

    private void TrySignal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_canSpeak())
                    {
                        lock (_queueLock) _queue.Clear();
                        lock (_directDispatchLock)
                        {
                            if (!_voice.IsActivelySpeaking)
                                SetState(SpeechState.Idle);
                        }
                        break;
                    }

                    bool hasPending;
                    lock (_queueLock) hasPending = _queue.Count > 0;
                    bool wasSpeakingAtDequeue = _voice.IsActivelySpeaking;
                    if (hasPending && !_voice.SupportsContinuationQueue && wasSpeakingAtDequeue)
                    {
                        SetState(SpeechState.Chaining);
                        int waitGen = Volatile.Read(ref _interruptGen);
                        if (!await WaitUntilNotActivelySpeakingAsync(waitGen, ct).ConfigureAwait(false))
                            break;
                    }

                    SpeechIntent? next;
                    int dispatchGen;
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    lock (_queueLock)
                    {
                        dispatchGen = Volatile.Read(ref _interruptGen);
                        for (int i = _queue.Count - 1; i >= 0; i--)
                        {
                            if ((now - _queue[i].EffectiveCreatedAtUtc).TotalSeconds > _queue[i].FreshnessTtlSeconds)
                                _queue.RemoveAt(i);
                        }

                        if (_queue.Count == 0)
                        {
                            next = null;
                        }
                        else
                        {
                            int bestIdx = 0;
                            for (int i = 1; i < _queue.Count; i++)
                            {
                                if (PriorityScore(_queue[i]) > PriorityScore(_queue[bestIdx]))
                                    bestIdx = i;
                            }
                            next = _queue[bestIdx];
                            _queue.RemoveAt(bestIdx);
                        }
                    }

                    if (next is null)
                    {
                        lock (_directDispatchLock)
                        {
                            if (!_voice.IsActivelySpeaking)
                                SetState(SpeechState.Idle);
                        }
                        break;
                    }

                    if (Volatile.Read(ref _interruptGen) != dispatchGen)
                        continue;

                    // Avoid talking over transient game explosions or Discord comms
                    double gapThreshold = (next.Urgency == UrgencyLevel.High || next.IsDirectUserResponse || next.InterruptCurrent) ? 0.72 : 0.42;
                    if (_audioMonitor.SampleOtherAudio().OtherSessionsPeak01 >= gapThreshold)
                    {
                        bool shouldWaitGap = false;
                        lock (_directDispatchLock)
                        {
                            if (!ct.IsCancellationRequested && !_disposed && Volatile.Read(ref _interruptGen) == dispatchGen && _canSpeak())
                            {
                                SetState(SpeechState.WaitingAudioGap);
                                shouldWaitGap = true;
                            }
                        }
                        if (!shouldWaitGap)
                            continue;

                        await WaitForGameAudioGapInternalAsync(_audioMonitor, next.Urgency, next.IsDirectUserResponse, next.InterruptCurrent, dispatchGen, ct).ConfigureAwait(false);
                    }

                    while (!ct.IsCancellationRequested && !_disposed && Volatile.Read(ref _interruptGen) == dispatchGen)
                    {
                        // If a direct chat answer started playing during WaitForGameAudioGapAsync on a non-queueing voice, wait gently
                        if (!_voice.SupportsContinuationQueue && !next.InterruptCurrent && _voice.IsActivelySpeaking)
                        {
                            SetState(SpeechState.Chaining);
                            if (!await WaitUntilNotActivelySpeakingAsync(dispatchGen, ct).ConfigureAwait(false))
                                break;
                            wasSpeakingAtDequeue = true;
                            continue;
                        }

                        lock (_directDispatchLock)
                        {
                            if (ct.IsCancellationRequested || _disposed || Volatile.Read(ref _interruptGen) != dispatchGen || !_canSpeak())
                            {
                                if (!_voice.IsActivelySpeaking)
                                    SetState(SpeechState.Idle);
                                break;
                            }

                            if (!_voice.SupportsContinuationQueue && !next.InterruptCurrent && _voice.IsActivelySpeaking)
                            {
                                // Another thread started speaking right as we acquired lock -> loop around and wait smoothly
                            }
                            else
                            {
                                DateTimeOffset speakAt = DateTimeOffset.UtcNow;
                                var gapSincePrev = _lastSpokenFinishedAtUtc == DateTimeOffset.MinValue
                                    ? TimeSpan.FromMinutes(1)
                                    : speakAt - _lastSpokenFinishedAtUtc;

                                bool chained = !next.InterruptCurrent && (wasSpeakingAtDequeue || gapSincePrev.TotalSeconds <= 4.5);
                                string utterance = _connectorResolver.BridgeUtterance(new ConnectorContext(
                                    _lastSpokenText,
                                    next,
                                    gapSincePrev,
                                    WasQueuedWhileSpeaking: wasSpeakingAtDequeue,
                                    IsEnglish: _isEn(),
                                    RotationIndex: Interlocked.Increment(ref _connectorRot)));

                                if (!string.IsNullOrWhiteSpace(utterance))
                                {
                                    bool chain = !next.InterruptCurrent;
                                    SetState(chained ? SpeechState.Chaining : SpeechState.Speaking);
                                    _voice.SpeakOrQueue(utterance, chainIfSpeaking: chain, interruptCurrent: next.InterruptCurrent);
                                    UpdateSpokenStateLocked(utterance, chain);
                                }
                                else if (!_voice.IsActivelySpeaking)
                                {
                                    SetState(SpeechState.Idle);
                                }
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _onLog?.Invoke($"Error cola de voz: {ex.Message}");
                    break;
                }
            }
        }
    }

    private async Task<bool> WaitUntilNotActivelySpeakingAsync(int expectedGen, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested &&
               !_disposed &&
               _voice.IsActivelySpeaking &&
               Volatile.Read(ref _interruptGen) == expectedGen &&
               sw.Elapsed.TotalSeconds < 6.0)
        {
            await Task.Delay(35, ct).ConfigureAwait(false);
        }
        return !ct.IsCancellationRequested &&
               !_disposed &&
               Volatile.Read(ref _interruptGen) == expectedGen &&
               !_voice.IsActivelySpeaking;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;
        _disposed = true;
        InterruptAndClear();
        try { _disposeCts.Cancel(); } catch { }
        TrySignal();
        try { await _workerTask.ConfigureAwait(false); } catch { }
        _signal.Dispose();
        _disposeCts.Dispose();
    }
}
