using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Monitor.Ingestion.Tests;

[Collection("stack")]
public class LifecycleTests(StackFixture s)
{
    // A retained "online" status is delivered the moment the worker subscribes, and the
    // telemetry and status filters go out in one SUBSCRIBE. Once the device_status row exists,
    // the worker is therefore subscribed to telemetry too. No repeated warm-up publishes needed.
    private async Task WaitUntilSubscribedAsync(Publisher pub)
    {
        var marker = s.NewDevice();
        await pub.PublishAsync(s.StatusTopic(marker), "online", retain: true);
        await s.EventuallyAsync(async () =>
            await s.ScalarAsync<long>("SELECT count(*) FROM device_status WHERE device = @d", ("d", marker)) == 1,
            "the worker to subscribe", 40);
    }

    [Fact]
    public async Task Shutdown_flushes_samples_that_are_still_queued()
    {
        const int N = 2500;
        await using var broker = await TestBroker.StartAsync();
        await using var pub = await Publisher.ConnectAsync(broker.Host, broker.Port);

        // A 60 s flush window and a huge batch size: the writer will hold everything it has read
        // and write nothing on its own during this test. Only the shutdown flush can store it.
        using var host = WorkerHarness.Build(broker.Host, broker.Port, s.PgConnectionString,
                                             batchSize: 10_000, flushIntervalMs: 60_000);
        var stopped = false;
        try
        {
            await host.StartAsync();
            await WaitUntilSubscribedAsync(pub);

            var stats = host.Services.GetRequiredService<IngestStats>();
            var baseline = stats.Snapshot().Received;

            var dev = s.NewDevice();
            var t = s.Topic(dev, "vibration");
            var baseTs = StackFixture.NowMs() - 60_000;
            for (var i = 0; i < N; i += 50)
                await Task.WhenAll(Enumerable.Range(i, Math.Min(50, N - i)).Select(k =>
                    pub.PublishAsync(t, StackFixture.Sample(dev, "vibration", k, baseTs + k, "mm/s"))));

            // Received is counted in the MQTT handler right before the sample is enqueued.
            await s.EventuallyAsync(() => Task.FromResult(stats.Snapshot().Received >= baseline + N),
                                    $"{N} samples to reach the worker", 30);

            await Task.Delay(700);   // give a wrong early flush the chance to happen
            Assert.Equal(0, await s.CountAsync(dev));   // premise: nothing was written yet

            await host.StopAsync();
            stopped = true;

            Assert.Equal(N, await s.CountAsync(dev));
            Assert.Equal(0, stats.Snapshot().Dropped);
        }
        finally
        {
            if (!stopped) await host.StopAsync();
        }
    }

    [Fact]
    public async Task Worker_reconnects_and_resubscribes_after_a_broker_restart()
    {
        var port = TestBroker.FreePort();
        var broker = await TestBroker.StartAsync(port);
        Publisher? pub = null;
        using var host = WorkerHarness.Build(broker.Host, port, s.PgConnectionString);
        try
        {
            await host.StartAsync();
            pub = await Publisher.ConnectAsync(broker.Host, port);
            await WaitUntilSubscribedAsync(pub);

            var before = s.NewDevice();
            await pub.PublishAsync(s.Topic(before, "temperature"),
                StackFixture.Sample(before, "temperature", 70, StackFixture.NowMs() - 2000));
            await s.EventuallyAsync(async () => await s.CountAsync(before) == 1, "a sample before the outage");

            // Kill the broker. All client connections drop, and the new broker has no memory
            // of the old session or its subscriptions.
            await pub.DisposeAsync();
            pub = null;
            await broker.DisposeAsync();

            // Stay down long enough for the worker to fail at least one reconnect attempt,
            // so the retry/backoff path runs, not just a lucky instant reconnect.
            await Task.Delay(3000);

            await using var broker2 = await TestBroker.StartAsync(port);
            await using var pub2 = await Publisher.ConnectAsync(broker2.Host, port);

            // Backoff is 1, 2, 4, 8 ... s, so the worker is back within seconds of the broker.
            await WaitUntilSubscribedAsync(pub2);

            var after = s.NewDevice();
            await pub2.PublishAsync(s.Topic(after, "temperature"),
                StackFixture.Sample(after, "temperature", 71, StackFixture.NowMs() - 1000));
            await s.EventuallyAsync(async () => await s.CountAsync(after) == 1, "a sample after the restart");

            Assert.Equal(1, await s.CountAsync(before));   // earlier data untouched
            await host.StopAsync();                         // and it still shuts down cleanly
        }
        finally
        {
            if (pub is not null) await pub.DisposeAsync();
        }
    }
}