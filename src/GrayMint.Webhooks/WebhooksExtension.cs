using System.Data.Common;
using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMint.Webhooks;

/// <summary>Registers the module in a host, and makes its tables.</summary>
public static class WebhooksExtension
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers what every host of the module uses: <see cref="WebhookMessageService"/>, for a scope, which
        /// queues the messages and reads how they stand. Its context comes from <c>AddGrayMintWebhooksDb</c>. The
        /// host doesn't start without that, on a database without the tables, or with tables of another version.
        /// </summary>
        /// <returns>The services, to register more.</returns>
        public IServiceCollection AddGrayMintWebhooks()
        {
            // each of them once, whoever asks: the delivery's registration asks too
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton(new WebhookDeliverySignal());
            services.TryAddScoped(serviceProvider => new WebhookMessageService(
                serviceProvider.GetRequiredService<WebhooksDbContext>(),
                serviceProvider.GetService<HostDatabase>(),
                serviceProvider.GetRequiredService<WebhookDeliverySignal>(),
                serviceProvider.GetRequiredService<TimeProvider>()));

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WebhooksStartup>());
            return services;
        }

        /// <summary>
        /// The tables on a connection of their own: in a database of their own, which the setup makes, or in the host's
        /// database, beside its tables, in a schema of their own (<see cref="WebhooksSchema.Name"/>). A message is
        /// then queued at once, whatever transaction the host has open.
        /// </summary>
        /// <param name="dbOptionsAction">
        /// Names the database, as in <c>options =&gt; options.UseNpgsql(connectionString)</c>.
        /// </param>
        /// <returns>The services, to register more.</returns>
        public IServiceCollection AddGrayMintWebhooksDb(Action<DbContextOptionsBuilder> dbOptionsAction)
        {
            return services.AddWebhooksDbContext((_, options) => dbOptionsAction(options));
        }

        /// <summary>
        /// The tables in the host's database, on the host's own connection: the host's context opens it, its
        /// connection interceptors with it, and a call runs in the database transaction the host's context has open,
        /// so a message is queued with the change it tells of, or not at all.
        /// </summary>
        /// <typeparam name="THostDbContext">The host's context, whose connection the module works on.</typeparam>
        /// <param name="dbOptionsAction">
        /// Gets that connection and names its database, as in
        /// <c>(connection, options) =&gt; options.UseNpgsql(connection)</c>.
        /// </param>
        /// <returns>The services, to register more.</returns>
        public IServiceCollection AddGrayMintWebhooksDb<THostDbContext>(
            Action<DbConnection, DbContextOptionsBuilder> dbOptionsAction)
            where THostDbContext : DbContext
        {
            services.AddScoped(serviceProvider =>
                new HostDatabase(serviceProvider.GetRequiredService<THostDbContext>().Database));

            return services.AddWebhooksDbContext((serviceProvider, options) => dbOptionsAction(
                serviceProvider.GetRequiredService<HostDatabase>().Database.GetDbConnection(), options));
        }

        /// <summary>
        /// Registers the delivery, in each host that delivers: <see cref="WebhookDeliveryService"/>, and a job that
        /// runs it when a message is queued in the host and every <see cref="WebhookDeliveryOptions.PollInterval"/>;
        /// and with them what <c>AddGrayMintWebhooks</c> registers. The host registers its
        /// <see cref="IWebhookSubscribers"/>, and doesn't start without it. At least one host delivers, or the
        /// messages only wait.
        /// </summary>
        /// <param name="configure">Sets the options; they are checked here, and a wrong one stops the registration.</param>
        /// <returns>The services, to register more.</returns>
        public IServiceCollection AddGrayMintWebhookDelivery(Action<WebhookDeliveryOptions>? configure = null)
        {
            var options = new WebhookDeliveryOptions();
            configure?.Invoke(options);
            options.Validate();

            // the start's check is registered before the job: a host that can't deliver stops before its first look
            services.AddGrayMintWebhooks();
            services.AddSingleton(Options.Create(options));
            services.AddSingleton(serviceProvider =>
                new WebhookSender(options, serviceProvider.GetRequiredService<TimeProvider>()));

            services.AddSingleton(serviceProvider => new WebhookTurn(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<WebhookSender>(),
                options,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<WebhookTurn>>()));

            services.AddSingleton(serviceProvider => new WebhookDeliveryService(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<WebhookTurn>(),
                options,
                serviceProvider.GetRequiredService<TimeProvider>()));

            services.AddHostedService<WebhookDeliveryJob>();
            return services;
        }

        private IServiceCollection AddWebhooksDbContext(Action<IServiceProvider, DbContextOptionsBuilder> dbOptionsAction)
        {
            // EF maps the non-generic DbContextOptions to the last context it registers, and a host's context that takes
            // them would get the module's: they stay the host's
            var hostOptions = services.Where(x => x.ServiceType == typeof(DbContextOptions)).ToArray();
            services.AddDbContext<WebhooksDbContext>(dbOptionsAction);
            foreach (var descriptor in services.Where(x => x.ServiceType == typeof(DbContextOptions)).Except(hostOptions).ToArray())
                services.Remove(descriptor);
            return services;
        }
    }

    /// <summary>
    /// Makes the tables when they are not there, and on a connection of the module's own the database too, before
    /// the host starts. It needs a login that may make them, and can run any number of times, one at a time: one
    /// host runs it, and the others check the tables at their start. It refuses a database with only some of the
    /// tables, or with tables of another version, and changes nothing in it.
    /// </summary>
    /// <param name="serviceProvider">The host's services, with the module registered.</param>
    /// <param name="cancellationToken">The token that cancels the setup.</param>
    /// <returns>The host's services.</returns>
    public static async Task<IServiceProvider> UseGrayMintWebhooks(this IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        await WebhooksSetup.Run(scope.ServiceProvider.GetRequiredService<WebhooksDbContext>(),
            scope.ServiceProvider.GetService<HostDatabase>(), cancellationToken);

        return serviceProvider;
    }
}
