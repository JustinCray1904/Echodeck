using System.Windows;

namespace EchoDeck;

// Discord "rich presence": while a song plays, Discord shows what is playing (e.g. "Playing: Flex" / "by Polo G") with the cover.
// Off unless switched on in Settings and an application ID is given; never in Anonymous Mode; songs only (not videos).
public partial class MainWindow
{
    private DiscordRpc? _discord;
    private string? _discordKey;
    private readonly OperationScope _discordScope = new();   // a late cover lookup of an older song (or after Anonymous Mode) is dropped, see OperationScope

    /// <summary>Called whenever the play / pause state may have changed; shows, updates or removes the Discord status.</summary>
    private void UpdateDiscord(bool playing)
    {
        try
        {
            bool wanted = _cfg.DiscordPresence && !_anon && !string.IsNullOrWhiteSpace(_cfg.DiscordClientId) &&
                          _currentIsAudio && _hasMedia && !_playbackEnded && _queueIndex >= 0 && _queueIndex < _queue.Count;
            if (!wanted)
            {
                ClearDiscord();
                return;
            }

            if (_discord == null || _discord.ClientId != _cfg.DiscordClientId.Trim())
            {
                _discord?.Dispose();
                _discord = new DiscordRpc(_cfg.DiscordClientId);
                _discordKey = null;
            }

            string path = _queue[_queueIndex].FullPath;
            string key = path + "|" + playing;
            if (key == _discordKey) return;
            _discordKey = key;

            var token = _discordScope.Renew();   // cancels the lookup of the previous status
            long elapsedMs = _player?.Time ?? 0;
            string detailsTemplate = _cfg.DiscordDetailsText, stateTemplate = _cfg.DiscordStateText;
            var discord = _discord;
            _ = Task.Run(async () =>
            {
                try { await PostDiscordAsync(discord, path, playing, elapsedMs, detailsTemplate, stateTemplate, token).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Write("Discord status", ex); }
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("Discord", ex);
        }
    }

    private async Task PostDiscordAsync(DiscordRpc discord, string path, bool playing, long elapsedMs, string detailsTemplate, string stateTemplate, CancellationToken token)
    {
        var tags = LibraryScanner.ReadTags(path);
        var (artist, title) = OnlineLyrics.Identity(path, tags);
        if (title.Length == 0) title = DisplayNames.Tidy(Path.GetFileNameWithoutExtension(path));

        string Fill(string template) => template.Replace("{title}", title).Replace("{artist}", artist).Trim();
        string details = Fill(string.IsNullOrWhiteSpace(detailsTemplate) ? "{title}" : detailsTemplate);
        string state = playing ? (artist.Length > 0 ? Fill(string.IsNullOrWhiteSpace(stateTemplate) ? "by {artist}" : stateTemplate) : "") : "Paused";
        long? start = playing ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - elapsedMs / 1000 : null;
        string? album = string.IsNullOrWhiteSpace(tags.Album) ? null : tags.Album;

        // first at once, without a picture ("tempo" is the name of a picture you can upload to the application in the Discord developer portal) ...
        _discordScope.RunIfCurrent(token, () =>
        {
            if (!_anon && _cfg.DiscordPresence)   // another song (or Anonymous Mode) may have taken over while the tags were read
                discord.Post(new DiscordActivity(details, state, start, "tempo", album ?? "Tempo"));
        });

        // ... then with the song's cover, if one can be found online (Discord can only show pictures that are on the web)
        var cover = await OnlineCovers.GetUrlAsync(path).ConfigureAwait(false);
        if (string.IsNullOrEmpty(cover)) return;
        // the lookup may have taken seconds: only publish if this song is still the one playing and Discord is still wanted
        _discordScope.RunIfCurrent(token, () =>
        {
            if (!_anon && _cfg.DiscordPresence)
                discord.Post(new DiscordActivity(details, state, start, cover, album ?? title));
        });
    }

    private void ClearDiscord()
    {
        _discordScope.Cancel();   // invalidates every lookup that is still running (and waits for a post in progress)
        if (_discord == null) return;
        _discordKey = null;
        _discord.Post(null);
        if (!_cfg.DiscordPresence || _anon)
        {
            // switched off: let go of the connection once the removal has been sent
            var discord = _discord;
            _discord = null;
            _ = Task.Run(async () => { await Task.Delay(2500).ConfigureAwait(false); discord.Dispose(); });
        }
    }
}
