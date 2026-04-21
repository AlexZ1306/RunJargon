using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class LocalArgosTranslationService : ITranslationService, IWarmableTranslationService, IDisposable
{
    private readonly string _pythonExecutable;
    private readonly string _scriptPath;
    private readonly SemaphoreSlim _bridgeLock = new(1, 1);

    private Process? _bridgeProcess;
    private StreamWriter? _bridgeInput;
    private StreamReader? _bridgeOutput;
    private bool _disposed;

    public LocalArgosTranslationService(string pythonExecutable, string scriptPath)
    {
        _pythonExecutable = pythonExecutable;
        _scriptPath = scriptPath;
    }

    public string DisplayName => "Argos Translate (offline)";

    public string ConfigurationHint =>
        "Перевод идет локально на этом ПК. Облако и банковская карта не нужны.";

    public async Task<TranslationResponse> TranslateAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync(
            new ArgosBridgeRequest(
                "translate",
                text,
                NormalizeSourceLanguage(sourceLanguage) ?? "en",
                NormalizeTargetLanguage(targetLanguage) ?? "ru"),
            cancellationToken);

        var note = string.IsNullOrWhiteSpace(result.Note)
            ? "Перевод выполнен офлайн на локальной машине."
            : result.Note;

        return new TranslationResponse(result.TranslatedText ?? string.Empty, DisplayName, note);
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new ArgosBridgeRequest("warmup", "Hello world", "en", "ru"),
            cancellationToken);
    }

    public static bool TryResolve(out LocalArgosTranslationService? service)
    {
        service = null;

        var workspaceRoot = AppContext.BaseDirectory;
        var scriptPath = Path.Combine(workspaceRoot, "Tools", "argos_bridge.py");
        if (!File.Exists(scriptPath))
        {
            return false;
        }

        var pythonCandidates = new[]
        {
            "python",
            "py"
        };

        foreach (var pythonCandidate in pythonCandidates)
        {
            if (CanStart(pythonCandidate))
            {
                service = new LocalArgosTranslationService(pythonCandidate, scriptPath);
                return true;
            }
        }

        return false;
    }

    private static bool CanStart(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _bridgeLock.Wait();
        try
        {
            DisposeBridgeProcess();
        }
        finally
        {
            _bridgeLock.Release();
            _bridgeLock.Dispose();
        }
    }

    private static string? NormalizeSourceLanguage(string? languageCode)
    {
        return languageCode switch
        {
            null or "" => "en",
            "en" => "en",
            "ru" => "ru",
            "de" => "de",
            "ja" => "ja",
            "zh-Hans" => "zh",
            _ => languageCode
        };
    }

    private static string? NormalizeTargetLanguage(string? languageCode)
    {
        return languageCode switch
        {
            null or "" => "ru",
            "en" => "en",
            "ru" => "ru",
            "de" => "de",
            "ja" => "ja",
            "zh-Hans" => "zh",
            _ => languageCode
        };
    }

    private async Task<ArgosBridgeResponse> SendRequestAsync(
        ArgosBridgeRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _bridgeLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureBridgeProcessAsync(cancellationToken);

            var payload = JsonSerializer.Serialize(request);
            await _bridgeInput!.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _bridgeInput.FlushAsync(cancellationToken);

            var responseLine = await _bridgeOutput!.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                DisposeBridgeProcess();
                throw new InvalidOperationException("Локальный Argos Translate не вернул корректный ответ.");
            }

            var result = JsonSerializer.Deserialize<ArgosBridgeResponse>(responseLine);
            if (result is null)
            {
                throw new InvalidOperationException("Локальный Argos Translate вернул пустой ответ.");
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                throw new InvalidOperationException($"Локальный Argos Translate не смог выполнить перевод: {result.Error}");
            }

            return result;
        }
        finally
        {
            _bridgeLock.Release();
        }
    }

    private async Task EnsureBridgeProcessAsync(CancellationToken cancellationToken)
    {
        if (_bridgeProcess is { HasExited: false })
        {
            return;
        }

        DisposeBridgeProcess();

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };

        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("utf8");
        startInfo.ArgumentList.Add(_scriptPath);
        startInfo.ArgumentList.Add("--serve");

        _bridgeProcess = new Process
        {
            StartInfo = startInfo
        };

        _bridgeProcess.Start();
        _bridgeInput = _bridgeProcess.StandardInput;
        _bridgeOutput = _bridgeProcess.StandardOutput;

        _ = Task.Run(async () =>
        {
            try
            {
                while (_bridgeProcess is { HasExited: false })
                {
                    var errorLine = await _bridgeProcess.StandardError.ReadLineAsync(cancellationToken);
                    if (errorLine is null)
                    {
                        break;
                    }

                    Debug.WriteLine($"Argos bridge: {errorLine}");
                }
            }
            catch
            {
            }
        }, cancellationToken);
    }

    private void DisposeBridgeProcess()
    {
        try
        {
            _bridgeInput?.Dispose();
        }
        catch
        {
        }

        try
        {
            _bridgeOutput?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_bridgeProcess is { HasExited: false })
            {
                _bridgeProcess.Kill(true);
            }
        }
        catch
        {
        }

        try
        {
            _bridgeProcess?.Dispose();
        }
        catch
        {
        }

        _bridgeInput = null;
        _bridgeOutput = null;
        _bridgeProcess = null;
    }

    private sealed record ArgosBridgeRequest(string Mode, string Text, string FromLanguage, string ToLanguage);

    private sealed record ArgosBridgeResponse(string? TranslatedText, string? Note, string? Error);
}
