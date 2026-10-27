namespace Shop.Notifications.Worker.Email;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string SenderEmail { get; set; } = "orders@shop.local";
}
