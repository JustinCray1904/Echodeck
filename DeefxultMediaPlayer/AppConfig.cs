namespace DeefxultMediaPlayer;

internal static class AppConfig
{
    /// <summary>PIN that applies until the user sets their own from the vault's gear button.</summary>
    public const string DefaultPin = "1234";

    /// <summary>
    /// The initial PIN: DefaultPin, or the DEEFXULT_PIN environment variable (4 digits) if set.
    /// Once a PIN is saved in the app (see PinStore) that saved PIN takes over.
    /// </summary>
    public static string Pin
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DEEFXULT_PIN");
            return env is { Length: 4 } && env.All(char.IsDigit) ? env : DefaultPin;
        }
    }
}
