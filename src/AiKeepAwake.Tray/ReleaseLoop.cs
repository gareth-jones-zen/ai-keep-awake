namespace AiKeepAwake.TrayApp;

/// <summary>The tray's daily look for a newer release (ReleaseCheck decides when, and whether at all: never when
/// right-hand manages the install). Half a minute after the tray starts, then hourly to see if a day has passed.</summary>
internal static class ReleaseLoop
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static void Start(Config config, Func<InstallRecord?> record, Action<ReleaseInfo?> found) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            while (true)
            {
                if (ReleaseCheck.Applies(record()))
                    found(await ReleaseCheck.Run(config, Fetch, Hook.Now()).ConfigureAwait(false));
                await Task.Delay(TimeSpan.FromHours(1)).ConfigureAwait(false);
            }
        });

    /// <summary>GitHub's latest release: a public repo, so no account or token; a generic User-Agent, as GitHub asks.</summary>
    private static async Task<string> Fetch(CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseCheck.Api);
        request.Headers.UserAgent.ParseAdd($"ai-keep-awake/{Cli.Version.Split('-')[0]}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
    }
}
