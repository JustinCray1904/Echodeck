namespace EchoDeck;

/// <summary>Imports single files or whole folders into the vault (shared by the pill and the settings page).</summary>
internal static class VaultImporter
{
    public sealed record Result(int Imported, int Failed, string? LastError);

    /// <summary>All supported media files inside <paramref name="folder"/> (recursive, unreadable folders skipped).</summary>
    public static List<string> CollectFolder(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(folder, "*", options)
            .Where(f => MediaTypes.IsSupportedExtension(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<Result> ImportAsync(
        VaultService service, IReadOnlyList<string> files, bool deleteOriginals,
        IProgress<string>? progress, Func<bool>? isCancelled = null, IProgress<double>? fraction = null)
    {
        int ok = 0, failed = 0;
        string? lastError = null;

        for (int i = 0; i < files.Count; i++)
        {
            if (isCancelled?.Invoke() == true) break;

            progress?.Report($"Encrypting {i + 1} of {files.Count}: {Path.GetFileName(files[i])}");
            try
            {
                await service.ImportAsync(files[i], deleteOriginals);
                ok++;
                fraction?.Report((i + 1.0) / files.Count);
            }
            catch (Exception ex)
            {
                failed++;
                lastError = Path.GetFileName(files[i]) + ": " + ex.Message;
            }
        }

        return new Result(ok, failed, lastError);
    }

    public static string Summary(Result r) =>
        r.Failed == 0
            ? (r.Imported == 1 ? "Imported 1 file (encrypted)" : $"Imported {r.Imported} files (encrypted)")
            : $"Imported {r.Imported}, {r.Failed} failed. {r.LastError}";
}
