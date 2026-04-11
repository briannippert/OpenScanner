using System.Diagnostics;
using OpenScanner.Server.Interfaces;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Devices;

namespace OpenScanner.Server.Services;

public class WhisperTranscriptionService : ITranscriptionService
{
    private readonly IDatabase _db;
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly IConfiguration _config;

    public WhisperTranscriptionService(IDatabase db, ILogger<WhisperTranscriptionService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _config = config;
    }

    public virtual string? TranscribeAudio(string audioPath)
    {
        // Check setting
        var enabled = _db.GetSettingAsync("EnableTranscription").GetAwaiter().GetResult();
        if (enabled != "true") return null;

        // Get model name from config (e.g. "small.en")
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

        var convertStart = new ProcessStartInfo("/usr/bin/ffmpeg")
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
        convertStart.ArgumentList.Add("-af");
        convertStart.ArgumentList.Add("volume=15dB");
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

        // 2. Run Whisper with Radio Context
        // Prompt helps Whisper bias towards radio terminology and style
        var prompt = "Dispatch, Unit 1, 10-4, copy, over. Priority traffic, code 3 response to street intersection. Suspect description: white male, blue jeans. License plate, vehicle registration, bolo. Structure fire, medical emergency, staging area. Status check, affirmative, negative, stand by. Channel 2, tac channel, command post. Alpha Adam, Bravo Boy, Charlie Charles, David, Edward, Frank, George, Henry, Ida, John, King, Lincoln, Mary, Nora, Ocean, Paul, Queen, Robert, Sam, Tom, Union, Victor, William, X-ray, Young, Zebra. 10-20 location, 10-8 in service, 10-7 out of service.";
        var whisperArgs = $"-m \"{modelPath}\" -f \"{tempWavPath}\" -nt -otxt -l en --prompt \"{prompt}\"";

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

                if (!proc.WaitForExit(120000)) // 120s timeout
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