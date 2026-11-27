using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

// Portable installs stay in place. A verified release is staged per user and takes over on the next launch.
sealed class ReleaseUpdater
{
    const string ApiUrl = "https://api.github.com/repos/diffsource/winBar/releases/latest";
    const long MaxDownloadBytes = 100 * 1024 * 1024;
    const long MaxEntryBytes = 50 * 1024 * 1024;
    static readonly HttpClient Client = CreateClient();
    static readonly Version CurrentVersion = typeof(Program).Assembly.GetName().Version ?? new Version(1, 0, 0);
    static readonly string Root = Path.Combine(Settings.Dir, "updates");
    static string ReadyFile => Path.Combine(Root, "ready.json");
    readonly SemaphoreSlim checkLock = new(1, 1);
    readonly CancellationTokenSource stop = new();
    readonly Action<string> report;
    readonly Func<bool> enabled;

    sealed record ReadyUpdate(string Version, string Folder);

    public ReleaseUpdater(Action<string> report, Func<bool> enabled)
    {
        this.report = report;
        this.enabled = enabled;
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("winBar/1.0 (+https://github.com/diffsource/winBar)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (text == null || !Regex.IsMatch(text, @"^v?\d+\.\d+\.\d+$")) return false;
        if (!Version.TryParse(text.TrimStart('v'), out var parsed)) return false;
        version = parsed;
        return true;
    }

    static bool HasMatchingVersion(string exe, Version version)
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        return Version.TryParse(fileVersion, out var actual) && actual == new Version(version.Major, version.Minor, version.Build, 0);
    }

    static ReadyUpdate? ReadReady()
    {
        try
        {
            var ready = JsonSerializer.Deserialize<ReadyUpdate>(File.ReadAllText(ReadyFile));
            if (ready == null || !TryVersion(ready.Version, out _) ||
                !Regex.IsMatch(ready.Folder, @"^v\d+\.\d+\.\d+-[0-9a-f]{32}$")) return null;
            return ready;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static bool LaunchStagedUpdate(string[] args)
    {
        var ready = ReadReady();
        if (ready == null || !TryVersion(ready.Version, out var version) || version <= CurrentVersion) return false;
        var directory = Path.Combine(Root, ready.Folder);
        var exe = Path.Combine(directory, "TaskbarTweaker.exe");
        if (!File.Exists(Path.Combine(directory, "TaskbarTap.dll")) || !File.Exists(exe) || !HasMatchingVersion(exe, version)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = directory,
                UseShellExecute = true,
                ArgumentList = { args.Contains("--folders") ? "--folders" : args.Contains("--tray") ? "--tray" : "--updated" }
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (enabled()) await CheckNowAsync(false).ConfigureAwait(false);
                try { await Task.Delay(TimeSpan.FromHours(12), stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Stop() => stop.Cancel();

    public static void DiscardReadyUpdate()
    {
        try { File.Delete(ReadyFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task CheckNowAsync(bool manual)
    {
        if (!await checkLock.WaitAsync(0).ConfigureAwait(false)) return;
        string? stagingDirectory = null;
        bool stagedSuccessfully = false;
        try
        {
            report("Suche nach GitHub-Releases…");
            using var response = await Client.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
            using var release = await JsonDocument.ParseAsync(body, cancellationToken: stop.Token).ConfigureAwait(false);
            var root = release.RootElement;
            if (!TryVersion(root.GetProperty("tag_name").GetString(), out var version)) throw new InvalidDataException("Invalid release version.");
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() || version <= CurrentVersion)
            {
                report($"Aktuell · v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}");
                return;
            }
            var ready = ReadReady();
            if (ready != null && TryVersion(ready.Version, out var stagedVersion) && stagedVersion >= version &&
                File.Exists(Path.Combine(Root, ready.Folder, "TaskbarTweaker.exe")))
            {
                report($"Update {ready.Version} ist beim nächsten Start bereit.");
                return;
            }
            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == "TaskbarTweaker.zip");
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Release ZIP is missing.");
            var url = asset.GetProperty("browser_download_url").GetString();
            var digest = asset.GetProperty("digest").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/diffsource/winBar/releases/download/", StringComparison.Ordinal) ||
                digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Release asset cannot be verified.");

            report($"Lade v{version.Major}.{version.Minor}.{version.Build} im Hintergrund…");
            Directory.CreateDirectory(Root);
            var folder = $"v{version.Major}.{version.Minor}.{version.Build}-{Guid.NewGuid():N}";
            var directory = Path.Combine(Root, folder);
            stagingDirectory = directory;
            Directory.CreateDirectory(directory);
            var zipPath = Path.Combine(directory, "release.zip");
            using (var download = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false))
            {
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidDataException("Release ZIP is too large.");
                await using var source = await download.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                await using var target = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > MaxDownloadBytes) throw new InvalidDataException("Release ZIP is too large.");
                    await target.WriteAsync(buffer.AsMemory(0, count), stop.Token).ConfigureAwait(false);
                }
            }
            await using (var zip = File.OpenRead(zipPath))
            {
                var hash = await SHA256.HashDataAsync(zip, stop.Token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(digest[7..])))
                    throw new InvalidDataException("Release checksum does not match GitHub.");
            }
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var expected = new HashSet<string>(StringComparer.Ordinal) { "TaskbarTweaker.exe", "TaskbarTap.dll" };
                if (archive.Entries.Count != 2 || archive.Entries.Any(e => !expected.Remove(e.FullName) || e.Length <= 0 || e.Length > MaxEntryBytes))
                    throw new InvalidDataException("Release ZIP has unexpected files.");
                foreach (var entry in archive.Entries) entry.ExtractToFile(Path.Combine(directory, entry.FullName));
            }
            File.Delete(zipPath);
            if (!HasMatchingVersion(Path.Combine(directory, "TaskbarTweaker.exe"), version))
                throw new InvalidDataException("Release version does not match its executable.");
            if (!manual && !enabled()) return;
            var marker = Path.Combine(Root, "ready.tmp");
            File.WriteAllText(marker, JsonSerializer.Serialize(new ReadyUpdate($"v{version.Major}.{version.Minor}.{version.Build}", folder)));
            File.Move(marker, ReadyFile, true);
            stagedSuccessfully = true;
            report($"Update v{version.Major}.{version.Minor}.{version.Build} ist beim nächsten Start bereit.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException or TaskCanceledException or FormatException or KeyNotFoundException)
        {
            if (manual) report($"Update-Prüfung fehlgeschlagen: {ex.Message}");
            else report("Update-Prüfung nicht möglich. Später erneut.");
        }
        finally
        {
            if (!stagedSuccessfully && stagingDirectory != null)
                try { Directory.Delete(stagingDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            checkLock.Release();
        }
    }
}
