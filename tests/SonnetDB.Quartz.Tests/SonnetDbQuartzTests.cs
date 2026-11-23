using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Listener;
using SonnetDB.Configuration;
using SonnetDB.Tests;
using Xunit;

namespace SonnetDB.Quartz.Tests;

public sealed class SonnetDbQuartzTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartPreservesJobAndPausedSimpleTriggerWithoutDuplicates(bool useRemoteServer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var cancellationToken = timeout.Token;
        string directory = Directory.CreateTempSubdirectory("sonnetdb-quartz-").FullName;
        WebApplication? server = null;
        string schedulerName = $"sonnetdb-test-{Guid.NewGuid():N}";
        var jobKey = new JobKey("device-check", "platform");
        var triggerKey = new TriggerKey("device-check", "platform");
        DateTimeOffset scheduled = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1800);

        try
        {
            string connectionString = $"Data Source={directory};Timeout=10";
            string? externalConnectionString = Environment.GetEnvironmentVariable("SONNETDB_QUARTZ_TEST_CONNECTION");
            if (useRemoteServer && !string.IsNullOrWhiteSpace(externalConnectionString))
            {
                connectionString = externalConnectionString;
            }
            else if (useRemoteServer)
            {
                var token = Guid.NewGuid().ToString("N");
                server = TestServerHost.Build(new ServerOptions
                {
                    DataRoot = directory,
                    Tokens = new Dictionary<string, string>(StringComparer.Ordinal) { [token] = "admin" },
                });
                await server.StartAsync(cancellationToken);
                var addresses = server.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>() ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
                connectionString = $"Data Source=sonnetdb+{addresses.Addresses.Single()}/quartz;Token={token};Timeout=10;Protocol=rest";
            }

            await SonnetDbQuartzSchema.EnsureCreatedAsync(connectionString, cancellationToken);
            var scheduler = await CreateSchedulerAsync(schedulerName, connectionString);
            try
            {
                var metadata = await scheduler.GetMetaData(cancellationToken);
                Assert.True(metadata.JobStoreSupportsPersistence);
                Assert.False(metadata.JobStoreClustered);
                var job = JobBuilder.Create<NoOpJob>().WithIdentity(jobKey).UsingJobData("probe", "present").Build();
                var trigger = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(jobKey)
                    .StartAt(scheduled).WithSimpleSchedule(value => value.WithIntervalInMinutes(1).RepeatForever()).Build();

                await scheduler.ScheduleJob(job, trigger, cancellationToken);
                await scheduler.PauseTrigger(triggerKey, cancellationToken);
                await scheduler.Start(cancellationToken);
            }
            finally
            {
                await StopSchedulerAsync(scheduler);
            }

            await SonnetDbQuartzSchema.EnsureCreatedAsync(connectionString, cancellationToken);
            scheduler = await CreateSchedulerAsync(schedulerName, connectionString);
            try
            {
                await scheduler.Start(cancellationToken);
                var job = await scheduler.GetJobDetail(jobKey, cancellationToken);
                Assert.NotNull(job);
                Assert.Equal("present", job.JobDataMap.GetString("probe"));
                var trigger = Assert.IsAssignableFrom<ISimpleTrigger>(await scheduler.GetTrigger(triggerKey, cancellationToken));
                Assert.Equal(scheduled, trigger.GetNextFireTimeUtc());
                Assert.Equal(TimeSpan.FromMinutes(1), trigger.RepeatInterval);
                Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(triggerKey, cancellationToken));
                Assert.Single(await scheduler.GetTriggersOfJob(jobKey, cancellationToken));
                await scheduler.ResumeTrigger(triggerKey, cancellationToken);
                Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, cancellationToken));

                var listener = new CompletionListener();
                scheduler.ListenerManager.AddJobListener(listener);
                var dueTrigger = trigger.GetTriggerBuilder().StartNow().Build();
                Assert.NotNull(await scheduler.RescheduleJob(triggerKey, dueTrigger, cancellationToken));
                Assert.Null(await listener.Completed.Task.WaitAsync(cancellationToken));
            }
            finally
            {
                await StopSchedulerAsync(scheduler);
            }
        }
        finally
        {
            if (server is not null)
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await server.StopAsync(shutdown.Token);
                await server.DisposeAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaInitializationHonorsPreCancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SonnetDbQuartzSchema.EnsureCreatedAsync("Data Source=unused", new CancellationToken(canceled: true)));
    }

    private static async Task<IScheduler> CreateSchedulerAsync(string schedulerName, string connectionString)
    {
        var builder = SchedulerBuilder.Create(schedulerName, "NON_CLUSTERED");
        builder.UsePersistentStore(store => store.UseSonnetDB(connectionString));
        return await builder.BuildScheduler();
    }

    private static async Task StopSchedulerAsync(IScheduler scheduler)
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await scheduler.Shutdown(waitForJobsToComplete: true, shutdown.Token);
    }

    public sealed class NoOpJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    private sealed class CompletionListener : JobListenerSupport
    {
        public TaskCompletionSource<JobExecutionException?> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string Name => nameof(CompletionListener);

        public override Task JobWasExecuted(
            IJobExecutionContext context,
            JobExecutionException? jobException,
            CancellationToken cancellationToken = default)
        {
            Completed.TrySetResult(jobException);
            return Task.CompletedTask;
        }
    }
}
