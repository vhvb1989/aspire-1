// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Oracle.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Oracle.EntityFrameworkCore;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring EntityFrameworkCore DbContext to Oracle database 
/// </summary>
public static class AspireOracleEFCoreExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Oracle:EntityFrameworkCore";
    private const DynamicallyAccessedMemberTypes RequiredByEF = DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties;

    /// <summary>
    /// Registers the given <see cref="DbContext" /> as a service in the services provided by the <paramref name="builder"/>.
    /// Enables db context pooling, retries, health check, logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext" /> that needs to be registered.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureDbContextOptions">An optional delegate to configure the <see cref="DbContextOptions"/> for the context.</param>
    /// <remarks>Reads the configuration from "Aspire:Oracle:EntityFrameworkCore:{typeof(TContext).Name}" config section, or "Aspire:Oracle:EntityFrameworkCore" if former does not exist.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="OracleEntityFrameworkCoreSettings.ConnectionString"/> is not provided.</exception>
    public static void AddOracleDatabaseDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<OracleEntityFrameworkCoreSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = GetDbContextSettings<TContext>(builder);

        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        builder.Services.AddDbContextPool<TContext>(ConfigureDbContext);

        ConfigureInstrumentation<TContext>(builder, settings);

        void ConfigureDbContext(DbContextOptionsBuilder dbContextOptionsBuilder)
        {
            if (string.IsNullOrEmpty(settings.ConnectionString))
            {
                throw new InvalidOperationException($"ConnectionString is missing. It should be provided in 'ConnectionStrings:{connectionName}' or under the 'ConnectionString' key in '{DefaultConfigSectionName}' or '{DefaultConfigSectionName}:{typeof(TContext).Name}' configuration section.");
            }

            dbContextOptionsBuilder.UseOracle(settings.ConnectionString, builder =>
            {
                // Resiliency:
                // Connection resiliency automatically retries failed database commands
                if (settings.Retry)
                {
                    builder.ExecutionStrategy(context => new OracleRetryingExecutionStrategy(context));
                }

                // The time in seconds to wait for the command to execute.
                if (settings.Timeout.HasValue)
                {
                    builder.CommandTimeout(settings.Timeout);
                }
            });

            configureDbContextOptions?.Invoke(dbContextOptionsBuilder);
        }
    }

    /// <summary>
    /// Configures retries, health check, logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="DbContext"/> is not registered in DI.</exception>
    public static void EnrichOracleDatabaseDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
            this IHostApplicationBuilder builder,
            Action<OracleEntityFrameworkCoreSettings>? configureSettings = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = GetDbContextSettings<TContext>(builder);

        configureSettings?.Invoke(settings);

        ConfigureRetry();

        ConfigureInstrumentation<TContext>(builder, settings);

        void ConfigureRetry()
        {
            if (!settings.Retry)
            {
                return;
            }

            // Resolving DbContext<TContextService> will resolve DbContextOptions<TContextImplementation>.
            // We need to replace the DbContextOptions service descriptor to inject more logic. This won't be necessary once
            // Aspire targets .NET 9 as EF will respect the calls to services.ConfigureDbContext<TContext>(). c.f. https://github.com/dotnet/efcore/pull/32518

            var oldDbContextOptionsDescriptor = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(DbContextOptions<TContext>));

            if (oldDbContextOptionsDescriptor is null)
            {
                throw new InvalidOperationException($"DbContext<{typeof(TContext).Name}> was not registered");
            }

            builder.Services.Remove(oldDbContextOptionsDescriptor);

            var dbContextOptionsDescriptor = new ServiceDescriptor(
                oldDbContextOptionsDescriptor.ServiceType,
                oldDbContextOptionsDescriptor.ServiceKey,
                factory: (sp, key) =>
                {
                    var dbContextOptions = oldDbContextOptionsDescriptor.ImplementationFactory?.Invoke(sp) as DbContextOptions<TContext>;

                    var optionsBuilder = dbContextOptions != null
                        ? new DbContextOptionsBuilder<TContext>(dbContextOptions)
                        : new DbContextOptionsBuilder<TContext>();

                    optionsBuilder.UseOracle(options => options.ExecutionStrategy(context => new OracleRetryingExecutionStrategy(context)));

                    return optionsBuilder.Options;
                },
                oldDbContextOptionsDescriptor.Lifetime
            );

            builder.Services.Add(dbContextOptionsDescriptor);
        }
    }

    private static void ConfigureInstrumentation<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(IHostApplicationBuilder builder, OracleEntityFrameworkCoreSettings settings) where TContext : DbContext
    {
        if (settings.Tracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder.AddEntityFrameworkCoreInstrumentation();
            });
        }

        if (settings.Metrics)
        {
            builder.Services.AddOpenTelemetry().WithMetrics(meterProviderBuilder =>
            {
                meterProviderBuilder.AddEventCountersInstrumentation(eventCountersInstrumentationOptions =>
                {
                    // https://github.com/dotnet/efcore/blob/a1cd4f45aa18314bc91d2b9ea1f71a3b7d5bf636/src/EFCore/Infrastructure/EntityFrameworkEventSource.cs#L45
                    eventCountersInstrumentationOptions.AddEventSources("Microsoft.EntityFrameworkCore");
                });
            });
        }

        if (settings.HealthChecks)
        {
            builder.TryAddHealthCheck(
                name: typeof(TContext).Name,
                static hcBuilder => hcBuilder.AddDbContextCheck<TContext>());
        }
    }

    private static OracleEntityFrameworkCoreSettings GetDbContextSettings<TContext>(IHostApplicationBuilder builder)
    {
        OracleEntityFrameworkCoreSettings settings = new();
        var typeSpecificSectionName = $"{DefaultConfigSectionName}:{typeof(TContext).Name}";
        var typeSpecificConfigurationSection = builder.Configuration.GetSection(typeSpecificSectionName);
        if (typeSpecificConfigurationSection.Exists()) // https://github.com/dotnet/runtime/issues/91380
        {
            typeSpecificConfigurationSection.Bind(settings);
        }
        else
        {
            builder.Configuration.GetSection(DefaultConfigSectionName).Bind(settings);
        }

        return settings;
    }
}
