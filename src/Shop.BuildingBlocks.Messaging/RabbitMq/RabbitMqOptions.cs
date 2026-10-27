namespace Shop.BuildingBlocks.Messaging.RabbitMq;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "shop.events";
    public string DeadLetterExchange { get; set; } = "shop.events.dlx";
    public string ClientProvidedName { get; set; } = "Shop.Messaging";
}
