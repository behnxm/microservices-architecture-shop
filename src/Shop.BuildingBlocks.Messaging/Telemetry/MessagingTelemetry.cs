using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Shop.BuildingBlocks.Messaging.Telemetry;

public static class MessagingTelemetry
{
    public const string ActivitySourceName = "Shop.Messaging";
    public const string MeterName = "Shop.Messaging";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> OutboxPublishedTotal = Meter.CreateCounter<long>(
        "shop_outbox_published_total",
        description: "Total number of outbox messages successfully published to RabbitMQ");

    public static readonly Counter<long> OutboxFailedTotal = Meter.CreateCounter<long>(
        "shop_outbox_failed_total",
        description: "Total number of outbox message publish failures");

    public static readonly Counter<long> ConsumerProcessedTotal = Meter.CreateCounter<long>(
        "shop_consumer_processed_total",
        description: "Total number of integration events successfully processed by consumers");

    public static readonly Counter<long> ConsumerFailedTotal = Meter.CreateCounter<long>(
        "shop_consumer_failed_total",
        description: "Total number of integration events that failed processing");

    public static readonly Histogram<double> ConsumerDurationSeconds = Meter.CreateHistogram<double>(
        "shop_consumer_duration_seconds",
        unit: "s",
        description: "Duration of event consumer processing in seconds");
}
