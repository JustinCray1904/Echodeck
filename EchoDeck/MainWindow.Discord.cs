using System.Windows;

namespace EchoDeck;

// Discord "rich presence": while a song plays, Discord shows what is playing (e.g. "Playing: Flex" / "by Polo G") with the cover.
// Off unless switched on in Settings and an application ID is given; never in Anonymous Mode; songs only (not videos).
public partial class MainWindow
{
    private DiscordRpc? _discord;
    private string? _discordKey;
    private readonly object _discordLock = new();
    private int _discordGen;   // bumped for every new status and every clear: a late cover update from an older song is dropped

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

            int gen;
            lock (_discordLock) gen = ++_discordGen;
            long elapsedMs = _player?.Time ?? 0;
            string detailsTemplate = _cfg.DiscordDetailsText, stateTemplate = _cfg.DiscordStateText;
            var discord = _discord;
            _ = Task.Run(async () =>
            {
                try { await PostDiscordAsync(discord, path, playing, elapsedMs, detailsTemplate, stateTemplate, gen).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Write("Discord status", ex); }
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("Discord", ex);
        }
    }

    private async Task PostDiscordAsync(DiscordRpc discord, string path, bool playing, long elapsedMs, string detailsTemplate, string stateTemplate, int gen)
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
        lock (_discordLock)
        {
            if (gen != _discordGen || _anon || !_cfg.DiscordPresence) return;   // another song (or Anonymous Mode) took over while the tags were read
            discord.Post(new DiscordActivity(details, state, start, "tempo", album ?? "Tempo"));
        }

        // ... then with the song's cover, if one can be found online (Discord can only show pictures that are on the web)
        var cover = await OnlineCovers.GetUrlAsync(path).ConfigureAwait(false);
        if (string.IsNullOrEmpty(cover)) return;
        lock (_discordLock)
        {
            // the lookup may have taken seconds: only publish if this song is still the one playing and Discord is still wanted
            if (gen != _discordGen || _anon || !_cfg.DiscordPresence) return;
            discord.Post(new DiscordActivity(details, state, start, cover, album ?? title));
        }
    }

    private void ClearDiscord()
    {
        lock (_discordLock) _discordGen++;   // invalidates every lookup that is still running
        if (_discord == null) return;
        _discordKey = null;
        lock (_discordLock) _discord.Post(null);
        if (!_cfg.DiscordPresence || _anon)
        {
            // switched off: let go of the connection once the removal has been sent
            var discord = _discord;
            _discord = null;
            _ = Task.Run(async () => { await Task.Delay(2500).ConfigureAwait(false); discord.Dispose(); });
        }
    }
}
