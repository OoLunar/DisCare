using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandAll;
using DSharpPlus.CommandAll.Parsers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OoLunar.DisCare.Events;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace OoLunar.DisCare
{
    public static class Program
    {
        private static readonly string[] _prefixes = [">>"];

        public static async Task Main(string[] args)
        {
            IServiceCollection services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(services => new ConfigurationBuilder()
                .AddJsonFile("config.json", true, true)
#if DEBUG
                .AddJsonFile("config.debug.json", true, true)
#endif
                .AddEnvironmentVariables("DisCare__")
                .Build());

            services.AddLogging(loggerBuilder =>
            {
                const string loggingFormat = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u4}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
                IConfiguration configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();
                LoggerConfiguration loggingConfiguration = new LoggerConfiguration()
                    .MinimumLevel.Is(configuration.GetValue("logging:level", LogEventLevel.Debug))
                    .WriteTo.Console(
                        outputTemplate: loggingFormat,
                        formatProvider: CultureInfo.InvariantCulture,
                        theme: new AnsiConsoleTheme(new Dictionary<ConsoleThemeStyle, string>
                        {
                            [ConsoleThemeStyle.Text] = "\x1b[0m",
                            [ConsoleThemeStyle.SecondaryText] = "\x1b[90m",
                            [ConsoleThemeStyle.TertiaryText] = "\x1b[90m",
                            [ConsoleThemeStyle.Invalid] = "\x1b[31m",
                            [ConsoleThemeStyle.Null] = "\x1b[95m",
                            [ConsoleThemeStyle.Name] = "\x1b[93m",
                            [ConsoleThemeStyle.String] = "\x1b[96m",
                            [ConsoleThemeStyle.Number] = "\x1b[95m",
                            [ConsoleThemeStyle.Boolean] = "\x1b[95m",
                            [ConsoleThemeStyle.Scalar] = "\x1b[95m",
                            [ConsoleThemeStyle.LevelVerbose] = "\x1b[34m",
                            [ConsoleThemeStyle.LevelDebug] = "\x1b[90m",
                            [ConsoleThemeStyle.LevelInformation] = "\x1b[36m",
                            [ConsoleThemeStyle.LevelWarning] = "\x1b[33m",
                            [ConsoleThemeStyle.LevelError] = "\x1b[31m",
                            [ConsoleThemeStyle.LevelFatal] = "\x1b[97;91m"
                        }))
                    .WriteTo.File(
                        path: $"logs/{DateTime.Now.ToUniversalTime().ToString("yyyy'-'MM'-'dd' 'HH'.'mm'.'ss", CultureInfo.InvariantCulture)}.log",
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: loggingFormat,
                        formatProvider: CultureInfo.InvariantCulture
                    );

                // Allow specific namespace log level overrides, which allows us to hush output from things like the database basic SELECT queries on the Information level.
                foreach (IConfigurationSection logOverride in configuration.GetSection("logging:overrides").GetChildren())
                {
                    if (!Enum.TryParse(logOverride.Value, out LogEventLevel logEventLevel))
                    {
                        continue;
                    }

                    loggingConfiguration.MinimumLevel.Override(logOverride.Key, logEventLevel);
                }

                loggerBuilder.AddSerilog(loggingConfiguration.CreateLogger());
            });

            Assembly currentAssembly = typeof(Program).Assembly;
            services.AddSingleton((services) =>
            {
                DiscordEventManager eventManager = new(services);
                eventManager.GatherEventHandlers(currentAssembly);
                return eventManager;
            });

            services.AddSingleton(async services =>
            {
                IConfiguration configuration = services.GetRequiredService<IConfiguration>();
                DiscordEventManager eventManager = services.GetRequiredService<DiscordEventManager>();
                DiscordShardedClient shardedClient = new(new DiscordConfiguration()
                {
                    Token = configuration.GetValue<string>("discord:token")!,
                    Intents = eventManager.Intents,
                    LoggerFactory = services.GetRequiredService<ILoggerFactory>()
                });

                eventManager.RegisterEventHandlers(shardedClient);
                IReadOnlyDictionary<int, CommandAllExtension> commandAllShards = await shardedClient.UseCommandAllAsync(new CommandAllConfiguration()
                {
#if DEBUG
                    DebugGuildId = configuration.GetValue<ulong?>("discord:debug_guild_id"),
#endif
                    PrefixParser = new PrefixParser(configuration.GetSection("discord:prefixes").Get<string[]>() ?? _prefixes)
                });

                foreach (CommandAllExtension commandAll in commandAllShards.Values)
                {
                    commandAll.CommandManager.AddCommands(commandAll, currentAssembly);
                    commandAll.ArgumentConverterManager.AddArgumentConverters(currentAssembly);
                    eventManager.RegisterEventHandlers(commandAll);
                }

                return shardedClient;
            });

            IServiceProvider serviceProvider = services.BuildServiceProvider();
            DiscordShardedClient discordShardedClient = await serviceProvider.GetRequiredService<Task<DiscordShardedClient>>();
            await discordShardedClient.StartAsync();
            await Task.Delay(-1);
        }
    }
}
