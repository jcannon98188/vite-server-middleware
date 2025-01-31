using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SpaServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Proggmatic.SpaServices.Vite.Npm;
using Proggmatic.SpaServices.Vite.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


namespace Proggmatic.SpaServices.Vite;

/// <summary>
/// Original template here: https://github.com/dotnet/aspnetcore/blob/main/src/Middleware/Spa/SpaServices.Extensions/src/ReactDevelopmentServer/ReactDevelopmentServerMiddleware.cs
/// </summary>
internal static class ViteMiddleware
{
    private const string LOG_CATEGORY_NAME = "Microsoft.AspNetCore.SpaServices";
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(5); // This is a development-time only feature, so a very long timeout is fine


    public static void Attach(ISpaBuilder spaBuilder, string scriptName, string cliRegex = "running in")
    {
        var pkgManagerCommand = spaBuilder.Options.PackageManagerCommand;
        var sourcePath = spaBuilder.Options.SourcePath;
        var devServerPort = spaBuilder.Options.DevServerPort;
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new ArgumentException("Cannot be null or empty", nameof(sourcePath));
        }

        if (string.IsNullOrEmpty(scriptName))
        {
            throw new ArgumentException("Cannot be null or empty", nameof(scriptName));
        }

        // Start Vite server and attach to middleware pipeline
        var appBuilder = spaBuilder.ApplicationBuilder;
        var applicationStoppingToken = appBuilder.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var logger = LoggerFinder.GetOrCreateLogger(appBuilder, LOG_CATEGORY_NAME);
        var diagnosticSource = appBuilder.ApplicationServices.GetRequiredService<DiagnosticSource>();
        var portTask = StartCreateViteAppServerAsync(sourcePath, scriptName, pkgManagerCommand, devServerPort, logger, diagnosticSource, applicationStoppingToken, cliRegex);

        // Everything we proxy is hardcoded to target http://localhost because:
        // - the requests are always from the local machine (we're not accepting remote
        //   requests that go directly to the Vite server)
        // - given that, there's no reason to use https, and we couldn't even if we
        //   wanted to, because in general the Vite server has no certificate
        var targetUriTask = portTask.ContinueWith(
            task => new UriBuilder("http", "localhost", task.Result).Uri, applicationStoppingToken);

        spaBuilder.UseProxyToSpaDevelopmentServer(() =>
        {
            // On each request, we create a separate startup task with its own timeout. That way, even if
            // the first request times out, subsequent requests could still work.
            var timeout = spaBuilder.Options.StartupTimeout;
            return targetUriTask.WithTimeout(timeout,
                "The Vite server did not start listening for requests " +
                $"within the timeout period of {timeout.TotalSeconds} seconds. " +
                "Check the log output for error information.");
        });
    }

    private static async Task<int> StartCreateViteAppServerAsync(
        string sourcePath, string scriptName, string pkgManagerCommand, int portNumber, ILogger logger, DiagnosticSource diagnosticSource, CancellationToken applicationStoppingToken, string cliRegex)
    {
        if (portNumber == default)
        {
            portNumber = TcpPortFinder.FindAvailablePort();
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation($"Starting Vite server on port {portNumber}...");
        }

        var envVars = new Dictionary<string, string>
        {
            { "PORT", portNumber.ToString() },
            { "BROWSER", "none" }, // We don't want Vite to open its own extra browser window pointing to the internal dev server port
        };
        var scriptRunner = new NodeScriptRunner(
            sourcePath, scriptName, null, envVars, pkgManagerCommand, diagnosticSource, applicationStoppingToken);
        scriptRunner.AttachToLogger(logger);

        using var stdErrReader = new EventedStreamStringReader(scriptRunner.StdErr);
        try
        {
            // Although the Vite dev server may eventually tell us the URL it's listening on,
            // it doesn't do so until it's finished compiling, and even then only if there were
            // no compiler warnings. So instead of waiting for that, consider it ready as soon
            // as it starts listening for requests.
            await scriptRunner.StdOut.WaitForMatch(
                new Regex(cliRegex, RegexOptions.None, RegexMatchTimeout));
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidOperationException(
                $"The {pkgManagerCommand} script '{scriptName}' exited without indicating that the " +
                "Vite server was listening for requests. The error output was: " +
                $"{stdErrReader.ReadAsString()}", ex);
        }

        return portNumber;
    }
}