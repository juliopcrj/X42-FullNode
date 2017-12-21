﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stratis.Bitcoin.Builder.Feature;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.Features.Dns
{
    /// <summary>
    /// Responsible for managing the DNS feature.
    /// </summary>
    public class DnsFeature : FullNodeFeature
    {
        /// <summary>
        /// Defines a back-off interval in milliseconds if the DNS server fails whilst operating, before it restarts.
        /// </summary>
        public const int DnsServerBackoffInterval = 2000;

        /// <summary>
        /// Defines the name of the masterfile on disk.
        /// </summary>
        public const string DnsMasterFileName = "masterfile.json";

        /// <summary>
        /// Defines the DNS server.
        /// </summary>
        private readonly IDnsServer dnsServer;
        
        /// <summary>
        /// Defines the whitelist manager.
        /// </summary>
        private readonly IWhitelistManager whitelistManager;

       /// <summary>
        /// Defines the logger.
        /// </summary>
        private readonly ILogger logger;

        /// <summary>
        /// Defines the node lifetime.
        /// </summary>
        private readonly INodeLifetime nodeLifetime;

        /// <summary>
        /// Defines the configuration settings for the node.
        /// </summary>
        private readonly NodeSettings nodeSettings;

        /// <summary>
        /// Defines the data folders of the system.
        /// </summary>
        private readonly DataFolder dataFolders;

        /// <summary>
        /// Defines the long running task used to support the DNS service.
        /// </summary>
        private Task dnsTask;

        /// <summary>
        /// Factory for creating background async loop tasks.
        /// </summary>
        private readonly IAsyncLoopFactory asyncLoopFactory;
        
        /// <summary>
        /// The async loop to refresh the whitelist.
        /// </summary>
        private IAsyncLoop whitelistRefreshLoop;
        /// <summary>
        /// Defines a flag used to indicate whether the object has been disposed or not.
        /// </summary>
        private bool disposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="DnsFeature"/> class.
        /// </summary>
        /// <param name="dnsServer">The DNS server.</param>
        /// <param name="whitelistManager">The whitelist manager.</param>
        /// <param name="loggerFactory">The factory to create the logger.</param>
        /// <param name="nodeLifetime">The node lifetime object used for graceful shutdown.</param>
        /// <param name="nodeSettings">The node settings object containing node configuration.</param>
        /// <param name="dataFolders">The data folders of the system.</param>
        /// <param name="asyncLoopFactory">The asynchronous loop factory.</param>
        public DnsFeature(IDnsServer dnsServer, IWhitelistManager whitelistManager, ILoggerFactory loggerFactory, INodeLifetime nodeLifetime, NodeSettings nodeSettings, DataFolder dataFolders, IAsyncLoopFactory asyncLoopFactory)
        {
            Guard.NotNull(dnsServer, nameof(dnsServer));
            Guard.NotNull(whitelistManager, nameof(whitelistManager));
            Guard.NotNull(loggerFactory, nameof(loggerFactory));
            Guard.NotNull(nodeLifetime, nameof(nodeLifetime));
            Guard.NotNull(nodeSettings, nameof(nodeSettings));
            Guard.NotNull(dataFolders, nameof(dataFolders));
            Guard.NotNull(asyncLoopFactory, nameof(asyncLoopFactory));

            this.dnsServer = dnsServer;
            this.whitelistManager = whitelistManager;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.asyncLoopFactory = asyncLoopFactory;
            this.nodeLifetime = nodeLifetime;
            this.nodeSettings = nodeSettings;
            this.dataFolders = dataFolders;
        }

        /// <summary>
        /// Initializes the DNS feature.
        /// </summary>
        public override void Initialize()
        {
            this.logger.LogTrace("()");

            // Create long running task for DNS service.
            this.dnsTask = Task.Factory.StartNew(this.RunDnsService, TaskCreationOptions.LongRunning);

            this.StartWhitelistRefreshLoop();

            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Runs the DNS service until the node is stopped.
        /// </summary>
        /// <returns>A task used to allow the caller to await the operation.</returns>
        private void RunDnsService()
        {
            this.logger.LogTrace("()");

            // Initialize DNS server.
            this.dnsServer.Initialize();

            bool restarted = false;

            while (true)
            {
                try
                {
                    // Only load if we are starting up for the first time.
                    if (!restarted)
                    {
                        // Load masterfile from disk if it exists.
                        string path = Path.Combine(this.dataFolders.DnsMasterFilePath, DnsMasterFileName);
                        if (File.Exists(path))
                        {
                            this.logger.LogInformation("Loading cached DNS masterfile from {0}", path);

                            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                IMasterFile masterFile = new DnsSeedMasterFile();
                                masterFile.Load(stream);

                                // Swap in masterfile from disk into DNS server.
                                this.dnsServer.SwapMasterfile(masterFile);
                            }
                        }
                    }

                    this.logger.LogInformation("Starting DNS server on port {0}", this.nodeSettings.DnsListenPort);

                    // Start.
                    this.dnsServer.ListenAsync(this.nodeSettings.DnsListenPort, this.nodeLifetime.ApplicationStopping).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Node shutting down, expected.
                    this.logger.LogInformation("Stopping DNS");
                    break;
                }
                catch (Exception e)
                {
                    this.logger.LogError(e, "Failed whilst running the DNS server with: {0}", e.Message);

                    try
                    {
                        // Back-off before restart.
                        Task.Delay(DnsServerBackoffInterval, this.nodeLifetime.ApplicationStopping).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        // Node shutting down, expected.
                        this.logger.LogInformation("Stopping DNS");
                        break;
                    }

                    this.logger.LogTrace("Restarting DNS server following previous failure.");

                    restarted = true;
                }
            }

            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Disposes of the object.
        /// </summary>
        public override void Dispose()
        {
            this.logger.LogInformation("Stopping DNS...");

            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes of the object.
        /// </summary>
        /// <param name="disposing"><c>true</c> if the object is being disposed of deterministically, otherwise <c>false</c>.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    IDisposable disposablewhitelistRefreshLoop = this.whitelistRefreshLoop as IDisposable;
                    disposablewhitelistRefreshLoop?.Dispose();
                }

                this.disposed = true;
            }
        }

        /// <summary>
        /// Starts the loop to refresh the whitelist.
        /// </summary>
        private void StartWhitelistRefreshLoop()
        {
            this.logger.LogTrace("()");

            this.whitelistRefreshLoop = this.asyncLoopFactory.Run($"{nameof(DnsFeature)}.WhitelistRefreshLoop", token =>
            {
                this.whitelistManager.RefreshWhitelist();
                return Task.CompletedTask;
            },
            this.nodeLifetime.ApplicationStopping,
            repeatEvery: new TimeSpan(0, 0, 30));

            this.logger.LogTrace("(-)");
        }
    }
}