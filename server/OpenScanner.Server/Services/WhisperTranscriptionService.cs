using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using OpenScanner.Server.Interfaces;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class WhisperTranscriptionService : ITranscriptionService, IDisposable
{
    private readonly IDatabase _db;
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly IConfiguration _config;

    private readonly System.Threading.Channels.Channel<(CallLog Log, string AudioPath)> _queueChannel = System.Threading.Channels.Channel.CreateUnbounded<(CallLog Log, string AudioPath)>();
    private readonly List<TranscriptionWorker> _workers = new();
    private readonly object _workersLock = new();
    private readonly CancellationTokenSource _cts = new();

    public event Action<CallLog>? OnTranscriptionCompleted;

    private class TranscriptionWorker
    {
        private readonly CancellationTokenSource _workerCts;
        public Task Task { get; }

        public TranscriptionWorker(Func<CancellationToken, Task> loop, CancellationToken parentToken)
        {
            _workerCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            Task = Task.Run(() => loop(_workerCts.Token));
        }

        public void Stop()
        {
            _workerCts.Cancel();
        }
    }

    public WhisperTranscriptionService(IDatabase db, ILogger<WhisperTranscriptionService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _config = config;

        // Initialize workers based on current setting
        var targetCount = GetTargetThreadCount();
        AdjustWorkers(targetCount);

        // Start background setting monitor
        _ = StartSettingsMonitor(_cts.Token);
    }

    private int GetTargetThreadCount()
    {
        try
        {
            var val = _db.GetSettingAsync("TranscriptionThreads").GetAwaiter().GetResult();
            if (int.TryParse(val, out var count))
            {
                return Math.Max(1, count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read TranscriptionThreads setting");
        }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    private void AdjustWorkers(int targetCount)
    {
        lock (_workersLock)
        {
            if (targetCount < 1) targetCount = 1;

            while (_workers.Count < targetCount)
            {
                var worker = new TranscriptionWorker(WorkerLoop, _cts.Token);
                _workers.Add(worker);
                _logger.LogInformation($"Started transcription worker. Total workers: {_workers.Count}");
            }

            while (_workers.Count > targetCount)
            {
                var lastIdx = _workers.Count - 1;
                var worker = _workers[lastIdx];
                worker.Stop();
                _workers.RemoveAt(lastIdx);
                _logger.LogInformation($"Stopped transcription worker. Total workers: {_workers.Count}");
            }
        }
    }

    private async Task StartSettingsMonitor(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, cancellationToken);
                var targetCount = GetTargetThreadCount();
                AdjustWorkers(targetCount);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in settings monitor loop");
            }
        }
    }

    private async Task WorkerLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for work
                var hasWork = await _queueChannel.Reader.WaitToReadAsync(cancellationToken);
                if (!hasWork) break;

                while (_queueChannel.Reader.TryRead(out var job))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _queueChannel.Writer.TryWrite(job);
                        break;
                    }

                    await ProcessJobAsync(job, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in transcription worker loop");
        }
    }

    private async Task ProcessJobAsync((CallLog Log, string AudioPath) job, CancellationToken cancellationToken)
    {
        try
        {
            // Run transcription
            var transcription = TranscribeAudio(job.AudioPath);

            // Update database
            await _db.UpdateTranscriptionAsync(job.Log.Id, transcription);

            // Update in-memory log object
            job.Log.Transcription = transcription;

            // Notify completion
            OnTranscriptionCompleted?.Invoke(job.Log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to process transcription for log {job.Log.Id}");
        }
    }

    public void QueueTranscription(CallLog log, string audioPath)
    {
        _queueChannel.Writer.TryWrite((log, audioPath));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();

        lock (_workersLock)
        {
            foreach (var worker in _workers)
            {
                worker.Stop();
            }
            _workers.Clear();
        }
    }

    // Default generic radio/dispatch prompt. Biases Whisper toward scanner
    // terminology and style. Overridable via Transcription:Prompt config.
    private const string DefaultPrompt = "Dispatch, Unit 1, 10-4, copy, over. Priority traffic, code 3 response to street intersection. Suspect description: white male, blue jeans. License plate, vehicle registration, bolo. Structure fire, medical emergency, staging area. Status check, affirmative, negative, stand by. Channel 2, tac channel, command post. Kilo, Tango, Zulu, X-ray. 10-20 location, 10-8 in service, 10-7 out of service.";

    // Voice-tuned ffmpeg filter chain applied before handing audio to Whisper:
    // band-limit to the narrowband voice range, denoise, and adaptively normalize
    // loudness (replaces a flat gain that clipped hot clips and under-boosted
    // quiet ones). Overridable via Transcription:AudioFilters config.
    private const string DefaultAudioFilters = "highpass=f=200,lowpass=f=3500,afftdn,dynaudnorm=f=200:g=5,alimiter=limit=0.95";

    // Build the whisper-cli argument string. Kept pure/static so it can be unit
    // tested without a real whisper binary or audio file.
    internal static string BuildWhisperArgs(string modelPath, string wavPath, string prompt, int beamSize, int threads, string? extraArgs)
    {
        // -nt: no timestamps, -otxt: write .txt, -l en: force English.
        // -bs/-bo: beam search (accuracy-first). -t: internal threads.
        // -mc 0: don't carry text context across 30s windows — radio clips are
        //   short/independent, so this removes a common hallucination/repetition
        //   path (equivalent to condition_on_previous_text=false).
        // -et 2.8: entropy threshold that keeps the temperature fallback which
        //   reduces garbage output on hard audio.
        var args = $"-m \"{modelPath}\" -f \"{wavPath}\" -nt -otxt -l en" +
                   $" -bs {beamSize} -bo {beamSize} -t {threads} -mc 0 -et 2.8" +
                   $" --prompt \"{prompt}\"";
        if (!string.IsNullOrWhiteSpace(extraArgs)) args += " " + extraArgs.Trim();
        return args;
    }

    public string? TranscribeAudio(string audioPath)
    {
        // Check setting
        var enabled = _db.GetSettingAsync("EnableTranscription").GetAwaiter().GetResult();
        if (enabled != "true") return null;

        // Get model name from config (e.g. "large-v3-turbo-q5_0")
        var modelName = _config["Transcription:Model"] ?? "small.en";

        // Temp file for resampling to 16k
        var tempWavPath = audioPath + ".16k.wav";
        // Robustly find whisper.cpp root
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        string? whisperRoot = null;

        // 1. Search up to find whisper.cpp
        for (int i = 0; i < 6; i++)
        {
            if (currentDir == null) break;
            var probe = Path.Combine(currentDir.FullName, "whisper.cpp");
            if (Directory.Exists(probe))
            {
                whisperRoot = probe;
                break;
            }

            probe = Path.Combine(currentDir.FullName, "../whisper.cpp");
            if (Directory.Exists(probe))
            {
                whisperRoot = Path.GetFullPath(probe);
                break;
            }

            currentDir = currentDir.Parent;
        }

        if (whisperRoot == null)
        {
            var projectRoot = Directory.GetCurrentDirectory();
            whisperRoot = Path.GetFullPath(Path.Combine(projectRoot, "../../whisper.cpp"));
        }

        var whisperBin = Path.Combine(whisperRoot, "build/bin/whisper-cli");
        var modelPath = Path.Combine(whisperRoot, $"models/ggml-{modelName}.bin");

        if (!File.Exists(whisperBin) || !File.Exists(modelPath))
        {
            _logger.LogError($"Whisper not found at {whisperBin} or model missing at {modelPath}. Search root was: {whisperRoot}");
            return null;
        }

        var convertStart = new ProcessStartInfo(PlatformTools.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (Path.GetExtension(audioPath).Equals(".raw", StringComparison.OrdinalIgnoreCase))
        {
            convertStart.ArgumentList.Add("-f");
            convertStart.ArgumentList.Add("s16le");
            convertStart.ArgumentList.Add("-ar");
            convertStart.ArgumentList.Add("48000"); // Raw is always 48k now
            convertStart.ArgumentList.Add("-ac");
            convertStart.ArgumentList.Add("1");
        }

        convertStart.ArgumentList.Add("-i");
        convertStart.ArgumentList.Add(audioPath);
        var audioFilters = _config["Transcription:AudioFilters"];
        if (string.IsNullOrWhiteSpace(audioFilters)) audioFilters = DefaultAudioFilters;
        convertStart.ArgumentList.Add("-af");
        convertStart.ArgumentList.Add(audioFilters);
        convertStart.ArgumentList.Add("-ar");
        convertStart.ArgumentList.Add("16000");
        convertStart.ArgumentList.Add("-ac");
        convertStart.ArgumentList.Add("1");
        convertStart.ArgumentList.Add(tempWavPath);
        convertStart.ArgumentList.Add("-y");

        using (var proc = Process.Start(convertStart))
        {
            if (proc != null)
            {
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    _logger.LogError($"FFmpeg conversion failed with exit code {proc.ExitCode}. Stderr: {stderr}");
                }
            }
        }

        if (!File.Exists(tempWavPath)) return null;

        // 2. Run Whisper with radio context + accuracy-oriented decode settings.
        var prompt = _config["Transcription:Prompt"];
        if (string.IsNullOrWhiteSpace(prompt)) prompt = DefaultPrompt;
        var beamSize = int.TryParse(_config["Transcription:BeamSize"], out var bs) && bs > 0 ? bs : 5;
        var threads = int.TryParse(_config["Transcription:WhisperThreads"], out var t) && t > 0
            ? t
            : Math.Max(1, Environment.ProcessorCount);
        var extraArgs = _config["Transcription:ExtraArgs"];
        var whisperArgs = BuildWhisperArgs(modelPath, tempWavPath, prompt, beamSize, threads, extraArgs);

        var whisperStart = new ProcessStartInfo(whisperBin, whisperArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = whisperRoot
        };

        try
        {
            using var proc = Process.Start(whisperStart);
            if (proc != null)
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                // A large accuracy-first model on a Pi can be well below real time;
                // allow more headroom than the old fixed 120s. Configurable.
                var timeoutMs = (int.TryParse(_config["Transcription:TimeoutSeconds"], out var ts) && ts > 0 ? ts : 300) * 1000;
                if (!proc.WaitForExit(timeoutMs))
                {
                    _logger.LogError("Whisper timed out");
                    proc.Kill();
                }
                else
                {
                    var stderr = stderrTask.Result;
                    var stdout = stdoutTask.Result;

                    if (proc.ExitCode != 0)
                    {
                        _logger.LogError($"Whisper failed with exit code {proc.ExitCode}.\nStderr: {stderr}\nStdout: {stdout}");
                    }
                    else
                    {
                        // Log debug info if no file created
                        if (!File.Exists(tempWavPath + ".txt"))
                        {
                            _logger.LogError($"Whisper finished but no output file.\nStderr: {stderr}\nStdout: {stdout}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running Whisper process");
        }

        File.Delete(tempWavPath); // Clean up WAV

        var txtPath = tempWavPath + ".txt";
        if (File.Exists(txtPath))
        {
            var text = File.ReadAllText(txtPath).Trim();
            File.Delete(txtPath);
            // Whisper sometimes outputs [BLANK_AUDIO] or metadata in brackets
            if (text.StartsWith("[") && text.EndsWith("]")) return null;
            return string.IsNullOrEmpty(text) ? null : text;
        }

        return null;
    }
}