﻿using System;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Database.Util;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.SqlReplication;
using Raven.Server.Json;
using Raven.Server.Json.Parsing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils.Metrics;
using Voron;

namespace Raven.Server.Documents
{
    public class DocumentDatabase : IResourceStore
    {
        private readonly CancellationTokenSource _databaseShutdown = new CancellationTokenSource();
        public readonly PatchDocument Patch;

        private readonly object _idleLocker = new object();

        public DocumentDatabase(string name, RavenConfiguration configuration, MetricsScheduler metricsScheduler = null)
        {
            Name = name;
            Configuration = configuration;

            Notifications = new DocumentsNotifications();
            DocumentsStorage = new DocumentsStorage(this);
            IndexStore = new IndexStore(this);
            SqlReplicationLoader = new SqlReplicationLoader(this);
            DocumentTombstoneCleaner = new DocumentTombstoneCleaner(this);

            Metrics = new MetricsCountersManager(metricsScheduler ?? new MetricsScheduler());
            Patch = new PatchDocument(this);
        }

        public string Name { get; }

        public string ResourceName => $"db/{Name}";

        public RavenConfiguration Configuration { get; }

        public CancellationToken DatabaseShutdown => _databaseShutdown.Token;

        public DocumentsStorage DocumentsStorage { get; private set; }

        public DocumentTombstoneCleaner DocumentTombstoneCleaner { get; private set; }

        public DocumentsNotifications Notifications { get; }

        public MetricsCountersManager Metrics { get; }

        public IndexStore IndexStore { get; private set; }

        public SqlReplicationLoader SqlReplicationLoader { get; private set; }

        public void Initialize()
        {
            DocumentsStorage.Initialize();
            InitializeInternal();
        }

        public void Initialize(StorageEnvironmentOptions options)
        {
            DocumentsStorage.Initialize(options);
            InitializeInternal();
        }

        private void InitializeInternal()
        {
            IndexStore.Initialize();
            SqlReplicationLoader.Initialize();
            DocumentTombstoneCleaner.Initialize();
        }

        public void Dispose()
        {
            _databaseShutdown.Cancel();

            SqlReplicationLoader?.Dispose();
            SqlReplicationLoader = null;

            IndexStore?.Dispose();
            IndexStore = null;

            DocumentTombstoneCleaner?.Dispose();
            DocumentTombstoneCleaner = null;

            DocumentsStorage?.Dispose();
            DocumentsStorage = null;
        }

        public void RunIdleOperations()
        {
            if (Monitor.TryEnter(_idleLocker) == false)
                return;

            try
            {
                IndexStore?.RunIdleOperations();
            }

            finally
            {
                Monitor.Exit(_idleLocker);
            }
        }

        public void AddAlert(Alert alert)
        {
            // Ignore for now, we are going to have a new implementation
            if (DateTime.UtcNow.Ticks != 0)
                return;

            if (string.IsNullOrEmpty(alert.UniqueKey))
                throw new ArgumentNullException(nameof(alert.UniqueKey), "Unique error key must be not null");

            DocumentsOperationContext context;
            using (DocumentsStorage.ContextPool.AllocateOperationContext(out context))
            using (var tx = context.OpenWriteTransaction())
            {
                var document = DocumentsStorage.Get(context, Constants.RavenAlerts);
                DynamicJsonValue alerts;
                long? etag = null;
                if (document == null)
                {
                    alerts = new DynamicJsonValue
                    {
                        [alert.UniqueKey] = new DynamicJsonValue
                        {
                            ["IsError"] = alert.IsError,
                            ["CreatedAt"] = alert.CreatedAt,
                            ["Title"] = alert.Title,
                            ["Exception"] = alert.Exception,
                            ["Message"] = alert.Message,
                            ["Observed"] = alert.Observed,
                        }
                    };
                }
                else
                {
                    etag = document.Etag;
                    var existingAlert = (BlittableJsonReaderObject) document.Data[alert.UniqueKey];
                    alerts = new DynamicJsonValue(document.Data)
                    {
                        [alert.UniqueKey] = new DynamicJsonValue
                        {
                            ["IsError"] = alert.IsError,
                            ["CreatedAt"] = alert.CreatedAt,
                            ["Title"] = alert.Title,
                            ["Exception"] = alert.Exception,
                            ["Message"] = alert.Message,
                            ["Observed"] = alert.Observed,
                            ["LastDismissedAt"] = existingAlert?["LastDismissedAt"],
                        }
                    };
                }

                var alertsDocument = context.ReadObject(alerts, Constants.RavenAlerts, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
                DocumentsStorage.Put(context, Constants.RavenAlerts, etag, alertsDocument);
                tx.Commit();
            }
        }
    }
}