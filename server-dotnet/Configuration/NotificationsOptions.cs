namespace AuroraFileBucket.Server.Configuration;

/// <summary>Notification configuration bound from the <c>Notifications</c> section.</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public EmailNotificationOptions Email { get; set; } = new();

    public TelegramNotificationOptions Telegram { get; set; } = new();

    public bool NotifyOnSuccess { get; set; } = true;

    public bool NotifyOnFailure { get; set; } = true;
}

public sealed class EmailNotificationOptions
{
    public bool Enabled { get; set; }

    public string Provider { get; set; } = "SMTP";

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string SmtpUser { get; set; } = string.Empty;

    public string SmtpPassword { get; set; } = string.Empty;

    public bool UseSsl { get; set; } = true;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Aurora File Bucket";

    /// <summary>Extra recipients (observers). The uploading user is always notified.</summary>
    public List<string> To { get; set; } = new();
}

public sealed class TelegramNotificationOptions
{
    public bool Enabled { get; set; }

    /// <summary>Name of the environment variable that holds the bot token.</summary>
    public string BotTokenEnvVar { get; set; } = "AURORA_TELEGRAM_BOT_TOKEN";

    public string DefaultChatId { get; set; } = string.Empty;
}
