﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.CommandLine.Binding;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Environment;
using Process = System.CommandLine.Invocation.Process;

namespace System.CommandLine.Builder
{
    public static class CommandLineBuilderExtensions
    {
        private static readonly Lazy<string> _assemblyVersion =
            new Lazy<string>(() => {
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var assemblyVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (assemblyVersionAttribute is null)
                {
                    return assembly.GetName().Version.ToString();
                }
                else
                {
                    return assemblyVersionAttribute.InformationalVersion;
                }
            });

        public static TBuilder AddArgument<TBuilder>(
            this TBuilder builder,
            Argument argument)
            where TBuilder : CommandBuilder
        {
            builder.AddArgument(argument);

            return builder;
        }

        public static TBuilder AddCommand<TBuilder>(
            this TBuilder builder,
            Command command)
            where TBuilder : CommandBuilder
        {
            builder.AddCommand(command);

            return builder;
        }

        public static TBuilder AddOption<TBuilder>(
            this TBuilder builder,
            Option option)
            where TBuilder : CommandBuilder
        {
            builder.AddOption(option);

            return builder;
        }

        public static TBuilder AddGlobalOption<TBuilder>(
            this TBuilder builder, 
            Option option)
            where TBuilder : CommandBuilder
        {
            builder.AddGlobalOption(option);

            return builder;
        }

        public static CommandLineBuilder CancelOnProcessTermination(this CommandLineBuilder builder)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                bool cancellationHandlingAdded = false;
                ManualResetEventSlim? blockProcessExit = null;
                ConsoleCancelEventHandler? consoleHandler = null;
                EventHandler? processExitHandler = null;

                context.CancellationHandlingAdded += (CancellationTokenSource cts) =>
                {
                    blockProcessExit = new ManualResetEventSlim(initialState: false);
                    cancellationHandlingAdded = true;
                    consoleHandler = (_, args) =>
                    {
                        cts.Cancel();
                        // Stop the process from terminating.
                        // Since the context was cancelled, the invocation should
                        // finish and Main will return.
                        args.Cancel = true;
                    };
                    processExitHandler = (_1, _2) =>
                    {
                        cts.Cancel();
                        // The process exits as soon as the event handler returns.
                        // We provide a return value using Environment.ExitCode
                        // because Main will not finish executing.
                        // Wait for the invocation to finish.
                        blockProcessExit.Wait();
                        Environment.ExitCode = context.ResultCode;
                    };
                    Console.CancelKeyPress += consoleHandler;
                    AppDomain.CurrentDomain.ProcessExit += processExitHandler;
                };

                try
                {
                    await next(context);
                }
                finally
                {
                    if (cancellationHandlingAdded)
                    {
                        Console.CancelKeyPress -= consoleHandler;
                        AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                        blockProcessExit!.Set();
                    }
                }
            }, MiddlewareOrderInternal.Startup);

            return builder;
        }

        public static CommandLineBuilder ConfigureConsole(
            this CommandLineBuilder builder,
            Func<BindingContext, IConsole> createConsole)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                context.BindingContext.ConsoleFactory = new AnonymousConsoleFactory(createConsole);
                await next(context);
            }, MiddlewareOrderInternal.ConfigureConsole);

            return builder;
        }

        public static CommandLineBuilder EnableDirectives(
            this CommandLineBuilder builder,
            bool value = true)
        {
            builder.EnableDirectives = value;
            return builder;
        }

        public static CommandLineBuilder EnablePosixBundling(
            this CommandLineBuilder builder,
            bool value = true)
        {
            builder.EnablePosixBundling = value;
            return builder;
        }

        public static CommandLineBuilder ParseResponseFileAs(
            this CommandLineBuilder builder,
            ResponseFileHandling responseFileHandling)
        {
            builder.ResponseFileHandling = responseFileHandling;
            return builder;
        }

        public static CommandLineBuilder RegisterWithDotnetSuggest(
            this CommandLineBuilder builder)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                var feature = new FeatureRegistration("dotnet-suggest-registration");

                await feature.EnsureRegistered(async () =>
                {
                    try
                    {
                        var currentProcessFullPath = Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                        var currentProcessFileNameWithoutExtension = Path.GetFileNameWithoutExtension(currentProcessFullPath);

                        var stdOut = new StringBuilder();
                        var stdErr = new StringBuilder();

                        var dotnetSuggestProcess = Process.StartProcess(
                            command: "dotnet-suggest",
                            args: $"register --command-path \"{currentProcessFullPath}\" --suggestion-command \"{currentProcessFileNameWithoutExtension}\"",
                            stdOut: value => stdOut.Append(value),
                            stdErr: value => stdOut.Append(value));

                        await dotnetSuggestProcess.CompleteAsync();

                        return $"{dotnetSuggestProcess.StartInfo.FileName} exited with code {dotnetSuggestProcess.ExitCode}{NewLine}OUT:{NewLine}{stdOut}{NewLine}ERR:{NewLine}{stdErr}";
                    }
                    catch (Exception exception)
                    {
                        return $"Exception during registration:{NewLine}{exception}";
                    }
                });

                await next(context);
            }, MiddlewareOrderInternal.RegisterWithDotnetSuggest);

            return builder;
        }

        public static CommandLineBuilder UseDebugDirective(
            this CommandLineBuilder builder)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                if (context.ParseResult.Directives.Contains("debug"))
                {
                    var process = Diagnostics.Process.GetCurrentProcess();

                    var processId = process.Id;

                    context.Console.Out.WriteLine($"Attach your debugger to process {processId} ({process.ProcessName}).");

                    while (!Debugger.IsAttached)
                    {
                        await Task.Delay(500);
                    }
                }

                await next(context);
            }, MiddlewareOrderInternal.DebugDirective);

            return builder;
        }

        public static CommandLineBuilder UseCultureDirective(
            this CommandLineBuilder builder)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                var directives = context.ParseResult.Directives;

                ApplyCultureFromWellKnownEnvironmentVariables();

                if (directives.Contains("invariantculture"))
                    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                if (directives.Contains("invariantuiculture"))
                    CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                if (directives.TryGetValues("culture", out var cultures))
                {
                    foreach (var cultureName in cultures.Select(c => c.Trim()))
                        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                }
                if (directives.TryGetValues("uiculture", out var uicultures))
                {
                    foreach (var cultureName in uicultures.Select(c => c.Trim()))
                        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                }
                await next(context);

                static void ApplyCultureFromWellKnownEnvironmentVariables()
                {
                    const string invariant_name = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";
                    const string uiinvariant_name = "DOTNET_SYSTEM_GLOBALIZATION_UIINVARIANT";
                    const string culture_name = "DOTNET_SYSTEM_GLOBALIZATION_CULTURE";
                    const string uiculture_name = "DOTNET_SYSTEM_GLOBALIZATION_UICULTURE";

                    if (GetEnvironmentVariable(invariant_name) is string invariant &&
                        ParseBooleanEnvironmentVariableValue(invariant))
                    {
                        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                    }
                    if (GetEnvironmentVariable(uiinvariant_name) is string uiinvariant &&
                        ParseBooleanEnvironmentVariableValue(uiinvariant))
                    {
                        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                    }
                    if (GetEnvironmentVariable(culture_name) is string culture)
                    {
                        CultureInfo? c = null;
                        try { c = CultureInfo.GetCultureInfo(culture); }
                        catch (CultureNotFoundException) { }
                        if (!(c is null))
                            CultureInfo.CurrentCulture = c;
                    }
                    if (GetEnvironmentVariable(uiculture_name) is string uiculture)
                    {
                        CultureInfo? c = null;
                        try { c = CultureInfo.GetCultureInfo(uiculture); }
                        catch (CultureNotFoundException) { }
                        if (!(c is null))
                            CultureInfo.CurrentUICulture = c;
                    }
                }

                static bool ParseBooleanEnvironmentVariableValue(string value)
                {
                    if (bool.TryParse(value, out bool boolResult))
                        return boolResult;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numResult))
                        return numResult != 0;
                    return false;
                }
            }, MiddlewareOrderInternal.CultureDirective);

            return builder;
        }

        public static CommandLineBuilder UseEnvironmentVariableDirective(
            this CommandLineBuilder builder)
        {
            builder.AddMiddleware((context, next) =>
            {
                if (context.ParseResult.Directives.TryGetValues("env", out var directives))
                {
                    foreach (var envDirective in directives)
                    {
                        var components = envDirective.Split(new[] { '=' }, count: 2);
                        var variable = components.Length > 0 ? components[0].Trim() : string.Empty;
                        if (string.IsNullOrEmpty(variable) || components.Length < 2)
                        {
                            continue;
                        }
                        var value = components[1].Trim();
                        SetEnvironmentVariable(variable, value);
                    }
                }

                return next(context);
            }, MiddlewareOrderInternal.EnvironmentVariableDirective);
            
            return builder;
        }

        public static CommandLineBuilder UseDefaults(this CommandLineBuilder builder)
        {
            return builder
                   .UseVersionOption()
                   .UseHelp()
                   .UseEnvironmentVariableDirective()
                   .UseParseDirective()
                   .UseDebugDirective()
                   .UseCultureDirective()
                   .UseSuggestDirective()
                   .RegisterWithDotnetSuggest()
                   .UseTypoCorrections()
                   .UseParseErrorReporting()
                   .UseExceptionHandler()
                   .CancelOnProcessTermination();
        }

        public static CommandLineBuilder UseExceptionHandler(
            this CommandLineBuilder builder,
            Action<Exception, InvocationContext>? onException = null)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (Exception exception)
                {
                    (onException ?? Default)(exception, context);
                }
            }, MiddlewareOrderInternal.ExceptionHandler);

            return builder;

            void Default(Exception exception, InvocationContext context)
            {
                context.Console.ResetTerminalForegroundColor();
                context.Console.SetTerminalForegroundRed();

                context.Console.Error.Write("Unhandled exception: ");
                context.Console.Error.WriteLine(exception.ToString());

                context.Console.ResetTerminalForegroundColor();

                context.ResultCode = 1;
            }
        }

        public static CommandLineBuilder UseHelp(this CommandLineBuilder builder)
        {
            return builder.UseHelp(new HelpOption());
        }

        internal static CommandLineBuilder UseHelp(
            this CommandLineBuilder builder,
            HelpOption helpOption)
        {
            if (builder.HelpOption is null)
            {
                builder.HelpOption = helpOption; 
                builder.Command.TryAddGlobalOption(helpOption);

                builder.AddMiddleware(async (context, next) =>
                {
                    if (!ShowHelp(context, builder.HelpOption))
                    {
                        await next(context);
                    }
                }, MiddlewareOrderInternal.HelpOption);
            }

            return builder;
        }

        public static TBuilder UseHelpBuilder<TBuilder>(this TBuilder builder, Func<BindingContext, IHelpBuilder> getHelpBuilder)
            where TBuilder : CommandLineBuilder
        {
            if (builder is null)
            {
                throw new ArgumentNullException(nameof(builder));
            }
            builder.HelpBuilderFactory = getHelpBuilder;
            return builder;
        }

        public static CommandLineBuilder UseMiddleware(
            this CommandLineBuilder builder,
            InvocationMiddleware middleware,
            MiddlewareOrder order = MiddlewareOrder.Default)
        {
            builder.AddMiddleware(
                middleware,
                order);

            return builder;
        }

        public static CommandLineBuilder UseMiddleware(
            this CommandLineBuilder builder,
            Action<InvocationContext> onInvoke,
            MiddlewareOrder order = MiddlewareOrder.Default)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                onInvoke(context);
                await next(context);
            }, order);

            return builder;
        }

        public static CommandLineBuilder UseParseDirective(
            this CommandLineBuilder builder)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                if (context.ParseResult.Directives.Contains("parse"))
                {
                    context.InvocationResult = new ParseDirectiveResult();
                }
                else
                {
                    await next(context);
                }
            }, MiddlewareOrderInternal.ParseDirective);

            return builder;
        }

        public static CommandLineBuilder UseParseErrorReporting(
            this CommandLineBuilder builder)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                if (context.ParseResult.Errors.Count > 0)
                {
                    context.InvocationResult = new ParseErrorResult();
                }
                else
                {
                    await next(context);
                }
            }, MiddlewareOrderInternal.ParseErrorReporting);
            return builder;
        }

        public static CommandLineBuilder UseSuggestDirective(
            this CommandLineBuilder builder)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                if (context.ParseResult.Directives.TryGetValues("suggest", out var values))
                {
                    int position;

                    if (values.FirstOrDefault() is { } positionString)
                    {
                        position = int.Parse(positionString);
                    }
                    else
                    {
                        position = context.ParseResult.RawInput?.Length ?? 0;
                    }

                    context.InvocationResult = new SuggestDirectiveResult(position);
                }
                else
                {
                    await next(context);
                }
            }, MiddlewareOrderInternal.SuggestDirective);

            return builder;
        }

        public static CommandLineBuilder UseTypoCorrections(
            this CommandLineBuilder builder, int maxLevenshteinDistance = 3)
        {
            builder.AddMiddleware(async (context, next) =>
            {
                if (context.ParseResult.UnmatchedTokens.Any() &&
                    context.ParseResult.CommandResult.Command.TreatUnmatchedTokensAsErrors)
                {
                    var typoCorrection = new TypoCorrection(maxLevenshteinDistance);
                    
                    typoCorrection.ProvideSuggestions(context.ParseResult, context.Console);
                }
                await next(context);
            }, MiddlewareOrderInternal.TypoCorrection);

            return builder;
        }

        public static CommandLineBuilder UseValidationMessages(
            this CommandLineBuilder builder,
            ValidationMessages validationMessages)
        {
            builder.ValidationMessages = validationMessages;
            return builder;
        }

        public static CommandLineBuilder UseVersionOption(
            this CommandLineBuilder builder)
        {
            if (builder.Command.Children.GetByAlias("--version") != null)
            {
                return builder;
            }

            var versionOption = new Option("--version", "Show version information");

            builder.AddOption(versionOption);

            builder.AddMiddleware(async (context, next) =>
            {
                if (context.ParseResult.HasOption(versionOption))
                {
                    context.Console.Out.WriteLine(_assemblyVersion.Value);
                }
                else
                {
                    await next(context);
                }
            }, MiddlewareOrderInternal.VersionOption);

            return builder;
        }

        private static bool ShowHelp(
            InvocationContext context,
            IOption helpOption)
        {
            if (context.ParseResult.FindResultFor(helpOption) != null)
            {
                context.InvocationResult = new HelpResult();
                return true;
            }

            return false;
        }
    }
}
